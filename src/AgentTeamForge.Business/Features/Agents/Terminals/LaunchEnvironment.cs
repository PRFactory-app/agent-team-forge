namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>
/// Environment allowlist for every Herdr invocation and launched pane (reviewed legacy rules,
/// characterized in D2). Agent-session and Herdr caller context can never be forwarded, even when
/// an operator names it explicitly, so a launch never targets the caller's own Herdr session and a
/// pane never inherits the launching agent's inbox credentials.
/// </summary>
static class LaunchEnvironment
{
    static readonly HashSet<string> Allowed =
    [
        "PATH", "HOME", "USER", "LOGNAME", "SHELL", "TERM", "COLORTERM", "LANG", "TZ",
        "XDG_RUNTIME_DIR", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME",
        "DISPLAY", "WAYLAND_DISPLAY", "DBUS_SESSION_BUS_ADDRESS", "CODEX_HOME", "CLAUDE_CONFIG_DIR",
    ];

    static readonly string[] AllowedPrefixes = ["LC_", "MISE_"];

    static readonly string[] NeverPrefixes = ["CLAUDE_CODE_", "CLAUDE_TEAMS_", "WIN_AGENT_TEAMS_", "HERDR_"];

    static readonly HashSet<string> Never = ["CLAUDECODE", "CLAUDE_PID", "CLAUDE_EFFORT", "AGENT_NAME", "AGENT_PARENT_NAME", "AGENT_SESSION_ID", "CODEX_THREAD_ID", "ATF_EXTERNAL_ONLY"];

    public static bool IsInheritedSessionContext(string name) =>
        name != "CLAUDE_CODE_OAUTH_TOKEN" && (Never.Contains(name) || NeverPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)));

    /// <summary>Removes every variable that is not allowlisted, or that carries agent-session/Herdr context.</summary>
    public static void Apply(IDictionary<string, string?> environment, string? extraAllowed)
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
}
