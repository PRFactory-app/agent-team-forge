using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>
/// The native sessions the agent processes under a pane's shell run right now (Linux /proc).
/// Claude registers each interactive process in &lt;config&gt;/sessions/&lt;pid&gt;.json with its current
/// session and process start time; Codex keeps its current rollout file open. Null means the
/// identity cannot be established (other platforms, unreadable /proc, Pi).
/// </summary>
internal static partial class LiveNativeSessions
{
    public static IReadOnlySet<string>? Read(InteractiveAgentKind kind, int shellPid, string? claudeRoot)
    {
        if (!OperatingSystem.IsLinux() || kind == InteractiveAgentKind.Pi || kind == InteractiveAgentKind.Claude && claudeRoot is null) { return null; }
        var processes = new Dictionary<int, (int Parent, string Start)>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && Stat(pid) is { } stat)
                {
                    processes[pid] = stat;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (!processes.ContainsKey(shellPid)) { return null; }
        var sessions = new HashSet<string>();
        var pending = new Queue<int>([shellPid]);
        while (pending.TryDequeue(out var pid))
        {
            foreach (var child in processes.Where(p => p.Value.Parent == pid).Select(p => p.Key)) { pending.Enqueue(child); }
            if ((kind == InteractiveAgentKind.Claude ? ClaudeSession(claudeRoot!, pid, processes[pid].Start) : CodexSession(pid)) is { } session)
            {
                sessions.Add(session);
            }
        }
        return sessions;
    }

    static string? ClaudeSession(string claudeRoot, int pid, string start)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(claudeRoot, "sessions", pid + ".json")));
            var root = json.RootElement;
            // A reused PID keeps a stale registry file; only this exact process's entry counts.
            return root.TryGetProperty("procStart", out var procStart) && procStart.ValueKind == JsonValueKind.String && procStart.GetString() == start
                && root.TryGetProperty("sessionId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    static string? CodexSession(int pid)
    {
        try
        {
            foreach (var fd in Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd"))
            {
                if (new FileInfo(fd).LinkTarget is { } target && Rollout().Match(Path.GetFileName(target)) is { Success: true } match)
                {
                    return match.Groups["id"].Value;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    static (int Parent, string Start)? Stat(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            // Fields 3.. follow comm (field 2), which may contain spaces and ')'.
            var rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return rest.Length > 19 && int.TryParse(rest[4 - 3], NumberStyles.None, CultureInfo.InvariantCulture, out var parent)
                ? (parent, rest[22 - 3]) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { return null; }
    }

    [GeneratedRegex(@"\Arollout-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-(?<id>.+)\.jsonl\z")]
    private static partial Regex Rollout();
}
