using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>
/// The native sessions the agent processes under a pane's shell run right now (Linux /proc, Darwin
/// kern.proc and libproc). Claude registers each interactive process in &lt;config&gt;/sessions/&lt;pid&gt;.json
/// with its current session and process start time; Codex keeps its current rollout files open. Null
/// means the identity cannot be established (Windows, Pi, or any process that could not be read).
/// </summary>
internal static partial class LiveNativeSessions
{
    public static IReadOnlySet<string>? Read(InteractiveAgentKind kind, int shellPid, string? claudeRoot)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) || kind == InteractiveAgentKind.Pi
            || kind == InteractiveAgentKind.Claude && claudeRoot is null) { return null; }
        // A process starting or exiting mid-scan makes one scan unknown; a few retries ride out churn.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { return Scan(kind, shellPid, claudeRoot); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException) { }
        }
        return null;
    }

    static HashSet<string>? Scan(InteractiveAgentKind kind, int shellPid, string? claudeRoot)
    {
        var processes = OperatingSystem.IsMacOS() ? DarwinProcesses() : LinuxProcesses();
        if (!processes.ContainsKey(shellPid)) { return null; }
        var sessions = new HashSet<string>();
        var pending = new Queue<int>([shellPid]);
        while (pending.TryDequeue(out var pid))
        {
            foreach (var child in processes.Where(p => p.Value.Parent == pid).Select(p => p.Key)) { pending.Enqueue(child); }
            if (kind == InteractiveAgentKind.Claude)
            {
                if (ClaudeSession(claudeRoot!, pid, processes[pid].Start) is { } session) { sessions.Add(session); }
            }
            else { sessions.UnionWith(CodexSessions(OperatingSystem.IsMacOS() ? DarwinProcess.OpenFiles(pid) : LinuxOpenFiles(pid))); }
        }
        return sessions;
    }

    static Dictionary<int, (int Parent, string? Start)> LinuxProcesses()
    {
        var processes = new Dictionary<int, (int Parent, string? Start)>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) { continue; }
            // An exited process runs no session. Any other failure, and any failure below for a
            // process under the shell, leaves the live set unknown.
            try { processes[pid] = Stat(pid); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
        }
        return processes;
    }

    // One kern.proc.all read. A zombie runs no session; a process whose start time is unreadable is
    // listed without one, so only its Claude registry (which needs that time) makes the set unknown.
    static Dictionary<int, (int Parent, string? Start)> DarwinProcesses()
    {
        var table = DarwinProcess.Table();
        if (table.Count == 0) { throw new IOException("the Darwin process table is unreadable"); }
        return table.Where(entry => !entry.IsZombie).ToDictionary(entry => entry.Pid,
            entry => (entry.ParentPid, entry.Token is { } token ? (token >> 20).ToString(CultureInfo.InvariantCulture) : null));
    }

    // No registry file: not a Claude TUI. An unreadable or malformed one throws (unknown).
    static string? ClaudeSession(string claudeRoot, int pid, string? start)
    {
        var path = Path.Combine(claudeRoot, "sessions", pid + ".json");
        if (!File.Exists(path)) { return null; }
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        var recorded = (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("procStart", out var procStart)
            ? OperatingSystem.IsMacOS() ? DarwinStart(procStart) : LinuxStart(procStart)
            : null) ?? throw new FormatException("Claude session registry without a valid process start");
        // A reused PID keeps a stale registry file; only a well-formed, different start time is stale.
        if (recorded != (start ?? throw new IOException("process start time is unreadable"))) { return null; }
        return root.TryGetProperty("sessionId", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } session
            ? session : throw new FormatException("Claude session registry without a session id");
    }

    // Linux /proc stat start time in clock ticks.
    static string? LinuxStart(JsonElement procStart) => (procStart.ValueKind switch
    {
        JsonValueKind.String => ulong.TryParse(procStart.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var text) ? text : (ulong?)null,
        JsonValueKind.Number => procStart.TryGetUInt64(out var number) ? number : null,
        _ => null,
    })?.ToString(CultureInfo.InvariantCulture);

    // On Darwin Claude records `ps -o lstart` in UTC ("Thu Oct  1 11:23:38 2026"): whole seconds since the epoch.
    static string? DarwinStart(JsonElement procStart) =>
        procStart.ValueKind == JsonValueKind.String
        && DateTime.TryParseExact(string.Join(' ', procStart.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            "ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var started)
            ? new DateTimeOffset(started, TimeSpan.Zero).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) : null;

    // Every rollout the process holds open: more than one is an ambiguous identity.
    static List<string> CodexSessions(IEnumerable<string> openFiles)
    {
        var sessions = new List<string>();
        foreach (var target in openFiles)
        {
            if (Rollout().Match(Path.GetFileName(target)) is { Success: true } match) { sessions.Add(match.Groups["id"].Value); }
        }
        return sessions;
    }

    static IEnumerable<string> LinuxOpenFiles(int pid) =>
        [.. Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd")
            .Select(fd => new FileInfo(fd).LinkTarget ?? throw new IOException("file descriptor closed during the probe"))];

    static (int Parent, string? Start) Stat(int pid)
    {
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        // Fields 3.. follow comm (field 2), which may contain spaces and ')'.
        var close = stat.LastIndexOf(')');
        var rest = close > 0 && close + 2 < stat.Length ? stat[(close + 2)..].Split(' ') : [];
        return rest.Length > 19 && int.TryParse(rest[4 - 3], NumberStyles.None, CultureInfo.InvariantCulture, out var parent)
            ? (parent, rest[22 - 3]) : throw new FormatException("unreadable /proc stat");
    }

    [GeneratedRegex(@"\Arollout-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-(?<id>.+)\.jsonl\z")]
    private static partial Regex Rollout();
}
