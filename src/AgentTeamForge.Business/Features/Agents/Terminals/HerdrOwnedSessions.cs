using System.Text.Json;
using AgentTeamForge.Business.Features.Agents.Backends;
using System.Text.Json.Serialization;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>Durable ownership proof for Herdr sessions left alive across interrupted turns.</summary>
public static class HerdrOwnedSessions
{
    public static (string Session, string? TabId, string? TabLabel)? Location(string stateRoot, string jobId)
    {
        foreach (var (_, session) in Read(stateRoot, _ => { }))
        {
            if (session.JobId == jobId) { return (session.SessionName, session.TabId, session.TabLabel); }
        }
        return null;
    }
    internal static string PathFor(InteractiveLaunch launch) => System.IO.Path.ChangeExtension(launch.BootstrapPath, ".owned.json");

    internal static string BootstrapForRecord(string recordPath) =>
        recordPath.EndsWith(".owned.json", StringComparison.Ordinal)
            ? recordPath[..^".owned.json".Length] + ".bootstrap"
            : throw new ArgumentException("invalid Herdr ownership record path", nameof(recordPath));

    internal static void Save(InteractiveLaunch launch, OwnedHerdrSession session)
    {
        var path = PathFor(launch);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(session with { JobId = launch.JobId, AgentName = launch.AgentName }, HerdrSessionJson.Default.OwnedHerdrSession));
        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
    }

    internal static void Delete(InteractiveLaunch launch) => File.Delete(PathFor(launch));

    /// <summary>The bootstrap file and the macOS shell proof beside it, once no live pane can still be proven by them.</summary>
    internal static void DeleteLaunchFiles(string bootstrapPath)
    {
        File.Delete(bootstrapPath);
        File.Delete(HerdrTerminal.ShellProofPath(bootstrapPath));
    }

    /// <summary>Preserve launch proof and fence its job; recovery never stops an interactive TUI.</summary>
    public static void Recover(string stateRoot, Action<string> fence, Action<string> log)
    {
        foreach (var (path, session) in Read(stateRoot, log))
        {
            if (session.JobId is { } jobId) { fence(jobId); }
            log($"recovery: preserved owned Herdr session {session.SessionName} ({path})");
        }
    }

    internal static IEnumerable<(string Path, OwnedHerdrSession Session)> Read(string stateRoot, Action<string> log)
    {
        var directory = System.IO.Path.Combine(stateRoot, "herdr");
        if (!Directory.Exists(directory)) { yield break; }
        foreach (var path in Directory.EnumerateFiles(directory, "*.owned.json"))
        {
            OwnedHerdrSession? session = null;
            try
            {
                session = JsonSerializer.Deserialize(File.ReadAllText(path), HerdrSessionJson.Default.OwnedHerdrSession)
                    ?? throw new HerdrLaunchException("invalid Herdr ownership record");
            }
            catch (Exception e) when (e is IOException or JsonException or HerdrLaunchException)
            {
                log($"warning: Herdr ownership record unreadable at {path}: {e.Message}");
            }
            if (session is not null)
            {
                var fileName = LegacyAgentName(path);
                session = session with { AgentName = session.AgentName is null || session.AgentName == fileName ? fileName : null };
                yield return (path, session);
            }
        }
    }

    static string? LegacyAgentName(string path)
    {
        var file = System.IO.Path.GetFileName(path);
        var name = file[..^".owned.json".Length];
        return ValidAgentName(name) ? name : null;
    }

    internal static bool ValidAgentName(string? name) =>
        name is not null && System.Text.RegularExpressions.Regex.IsMatch(name, @"\Aatf[0-9a-f]{20}\z");

    /// <summary>
    /// Startup sweep of bootstrap files (and their macOS shell proofs) whose pane is provably gone. With an ownership
    /// record, its server or pane shell must be gone or replaced. Without one, no live process may carry the bootstrap
    /// path, and where a platform shell hides its environment (macOS) the shell that reported this bootstrap must have
    /// exited. A file that might still prove a live pane, including one behind an unreadable record, is kept.
    /// </summary>
    public static int SweepStaleBootstraps(string stateRoot, Action<string> log) =>
        SweepStaleBootstraps(stateRoot, new HerdrProcessRunner(), LivePids, log);

    internal static int SweepStaleBootstraps(string stateRoot, IHerdrProcessRunner runner, Func<IReadOnlyList<int>?> livePids, Action<string> log)
    {
        var directory = System.IO.Path.Combine(stateRoot, "herdr");
        if (!Directory.Exists(directory)) { return 0; }
        var records = Read(stateRoot, _ => { }).ToDictionary(entry => BootstrapForRecord(entry.Path), entry => entry.Session, StringComparer.Ordinal);
        var unrecorded = new List<string>();
        var swept = 0;
        foreach (var bootstrap in Directory.EnumerateFiles(directory, "*.bootstrap"))
        {
            if (File.Exists(System.IO.Path.ChangeExtension(bootstrap, ".owned.json")))
            {
                if (records.TryGetValue(bootstrap, out var session) && HerdrTerminal.OwnedPaneIsGone(runner, session))
                {
                    DeleteLaunchFiles(bootstrap);
                    swept++;
                }
            }
            else { unrecorded.Add(bootstrap); }
        }
        if (unrecorded.Count > 0 && livePids() is { } pids)
        {
            var carried = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pid in pids)
            {
                if (runner.EnvironmentValue(pid, HerdrTerminal.BootstrapVariable) is { } value) { carried.Add(value); }
            }
            foreach (var bootstrap in unrecorded)
            {
                if (carried.Contains(bootstrap)) { continue; }
                if (runner.EnvironmentMayBeHidden
                    && (HerdrTerminal.ShellProof(bootstrap) is not { } shell || runner.Identity(shell) is not null || HerdrTerminal.PidMayBeAlive(shell)))
                {
                    continue;
                }
                DeleteLaunchFiles(bootstrap);
                swept++;
            }
        }
        foreach (var proof in Directory.EnumerateFiles(directory, "*.bootstrap.shell"))
        {
            if (!File.Exists(proof[..^".shell".Length])) { File.Delete(proof); }
        }
        if (swept > 0) { log($"recovery: removed {swept} Herdr bootstrap file(s) whose pane is gone"); }
        return swept;
    }

    static IReadOnlyList<int>? LivePids()
    {
        if (OperatingSystem.IsMacOS()) { return DarwinProcess.Pids() is { Count: > 0 } pids ? pids : null; }
        try
        {
            return [.. Directory.EnumerateDirectories("/proc")
                .Select(path => int.TryParse(System.IO.Path.GetFileName(path), out var pid) ? pid : 0).Where(pid => pid > 0)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Close restored bare-resume panes per session. Records are never edited: they fence their jobs.</summary>
    public static int SweepRestored(string stateRoot, Func<string, IReadOnlyList<OwnedHerdrSession>, int> closeInSession, Action<string> log)
    {
        var closed = 0;
        var groups = Read(stateRoot, log)
            .Select(r => r.Session)
            .Where(s => s.Shared && s.PaneId is not null && s.TabId is not null && s.AgentName is not null)
            .GroupBy(s => s.SessionName);
        foreach (var group in groups) { closed += closeInSession(group.Key, [.. group]); }
        return closed;
    }

    internal static void Forget(string stateRoot, IReadOnlyList<string> jobIds)
    {
        foreach (var (path, session) in Read(stateRoot, message => throw new HerdrLaunchException(message)))
        {
            if (session.JobId is not null && jobIds.Contains(session.JobId))
            {
                DeleteLaunchFiles(BootstrapForRecord(path));
                File.Delete(path + ".gone");
                File.Delete(path);
            }
        }
    }

    internal static bool Stop(string stateRoot, IReadOnlyList<string> jobIds, Action<OwnedHerdrSession> stop)
    {
        var stopped = false;
        foreach (var (path, session) in Read(stateRoot, message => throw new HerdrLaunchException(message)))
        {
            if (session.JobId is null || !jobIds.Contains(session.JobId)) { continue; }
            stop(session); // Verifies server start time and owner label; never falls back to a PID.
            stopped = true; // Keep proof until the caller commits fence release.
        }
        return stopped;
    }
}

[JsonSerializable(typeof(OwnedHerdrSession))]
internal partial class HerdrSessionJson : JsonSerializerContext;
