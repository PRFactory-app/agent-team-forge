using System.Text.Json;
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
            if (session.JobId is not null && jobIds.Contains(session.JobId)) { File.Delete(path); }
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
