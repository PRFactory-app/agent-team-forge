using System.Diagnostics;
using System.Text.Json.Nodes;

namespace AgentTeamForge.Tests.Support.ManagedDemo;

/// <summary>
/// Test-only characterization copy of the legacy Linux Herdr launch rules, for D7 to replace
/// with production code. Nothing here starts a process. Provenance (docs/spikes/char-d2-report.md):
/// <c>spikes/m0-interactive/src/AtfSpike/Herdr/{LaunchEnvironment,HerdrCli,HerdrOwnership}.cs</c>
/// at <c>6e06de7</c> (LaunchEnvironment/HerdrCli blobs identical at <c>78f1e06</c> and <c>8a5e385</c>).
/// Deviations: the environment seed is injected rather than inherited from this process, the
/// start-info builders are split out of <c>HerdrCli.Invoke</c>/<c>CreateOwnedServer</c> before
/// <c>Process.Start</c>, and <c>SpikeState</c> is reduced to the ownership fields teardown reads.
/// </summary>
public static class HerdrLaunchFixture
{
    // LaunchEnvironment.cs @ 6e06de7, verbatim sets.
    private static readonly HashSet<string> Allowed =
    [
        "PATH", "HOME", "USER", "LOGNAME", "SHELL", "TERM", "COLORTERM", "LANG", "TZ",
        "XDG_RUNTIME_DIR", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME",
        "DISPLAY", "WAYLAND_DISPLAY", "DBUS_SESSION_BUS_ADDRESS", "CODEX_HOME", "CLAUDE_CONFIG_DIR",
    ];

    private static readonly string[] AllowedPrefixes = ["LC_", "MISE_"];

    private static readonly string[] NeverPrefixes = ["CLAUDE_CODE_", "CLAUDE_TEAMS_", "WIN_AGENT_TEAMS_", "HERDR_"];

    private static readonly HashSet<string> Never = ["CLAUDECODE", "CLAUDE_PID", "CLAUDE_EFFORT", "AGENT_NAME", "AGENT_PARENT_NAME", "AGENT_SESSION_ID"];

    public static bool IsInheritedSessionContext(string name) =>
        Never.Contains(name) || NeverPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    public static void ApplyEnvironment(IDictionary<string, string?> environment, string? extraAllowed)
    {
        var extra = (extraAllowed ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        foreach (var key in environment.Keys.ToList())
        {
            var allowed = Allowed.Contains(key) || AllowedPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)) || extra.Contains(key);
            if (!allowed || IsInheritedSessionContext(key))
            {
                environment.Remove(key);
            }
        }
    }

    /// <summary>HerdrCli.Invoke @ 6e06de7 up to Process.Start; <paramref name="socketPath"/> null is RunGlobal.</summary>
    public static ProcessStartInfo CommandStartInfo(IReadOnlyDictionary<string, string?> seed, string? extraAllowed, string? socketPath, params string[] args)
    {
        var psi = new ProcessStartInfo("herdr")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        Seed(psi, seed, extraAllowed);
        if (socketPath is not null)
        {
            psi.Environment["HERDR_SOCKET_PATH"] = socketPath;
        }
        return psi;
    }

    /// <summary>
    /// HerdrCli.CreateOwnedServer @ 6e06de7: refuses any listed name, then builds the detached
    /// server launch. The session name travels as <c>$0</c>, never inside the script text.
    /// </summary>
    public static ProcessStartInfo OwnedServerStartInfo(JsonNode sessionList, string sessionName, IReadOnlyDictionary<string, string?> seed, string? extraAllowed)
    {
        if (!CanCreate(sessionList, sessionName))
        {
            throw new InvalidOperationException($"session {sessionName} already exists; refusing to adopt it");
        }
        var psi = new ProcessStartInfo("setsid") { UseShellExecute = false };
        foreach (var a in new[] { "-f", "sh", "-c", ServerScript, sessionName })
        {
            psi.ArgumentList.Add(a);
        }
        Seed(psi, seed, extraAllowed);
        return psi;
    }

    public const string ServerScript = "exec herdr --session \"$0\" server </dev/null >/dev/null 2>&1";

    /// <summary>Creation is allowed only for a name absent from Herdr's session list (running or stopped).</summary>
    public static bool CanCreate(JsonNode sessionList, string name) => Find(sessionList, name) is null;

    /// <summary>HerdrOwnership.Teardown @ 6e06de7 (stopped-session refusal included).</summary>
    public static TeardownDecision Teardown(OwnedSession? owned, string recordedSession, JsonNode sessionList, bool serverIdentityMatches, JsonNode? workspaces)
    {
        if (owned is null)
        {
            return new("no ownership record: session was not created by this spike version", false);
        }
        if (Find(sessionList, recordedSession) is not { } session)
        {
            return new("recorded session no longer exists", false);
        }
        if (session["running"] is not JsonValue r || !r.TryGetValue<bool>(out var running) || !running)
        {
            return new("recorded session is not running; its ownership cannot be proven, so it is left for manual handling", false);
        }
        if (!serverIdentityMatches)
        {
            return new("running server is not the recorded process (PID + start time)", false);
        }
        var labelled = workspaces?["result"]?["workspaces"] is JsonArray ws &&
            ws.OfType<JsonObject>().Any(w => w["label"]?.GetValue<string>() == owned.OwnerLabel);
        return labelled ? new(null, true) : new("owner label workspace missing from the running server", false);
    }

    private static void Seed(ProcessStartInfo psi, IReadOnlyDictionary<string, string?> seed, string? extraAllowed)
    {
        psi.Environment.Clear();
        foreach (var (key, value) in seed)
        {
            psi.Environment[key] = value;
        }
        ApplyEnvironment(psi.Environment, extraAllowed);
    }

    private static JsonObject? Find(JsonNode sessionList, string name) =>
        (sessionList["sessions"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(s => s["name"]?.GetValue<string>() == name);
}

public sealed record TeardownDecision(string? Problem, bool Stop);

public sealed record OwnedSession(string OwnerLabel, int ServerPid, long ServerStartTicks);
