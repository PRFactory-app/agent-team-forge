using System.Collections;

namespace AgentTeamForge.Host.Features.Setup;

internal static class DaemonEnvironment
{
    // A daemon can outlive the lead that started it, and its agents inherit
    // its environment. Keep user configuration and credentials (provider keys,
    // CODEX_HOME, CLAUDE_CONFIG_DIR, proxies, SSH/git, Windows essentials);
    // drop only the starting session's identity, wake targets, Herdr pane
    // targeting and ATF run markers.
    static readonly string[] DroppedPrefixes =
    [
        "CLAUDE_CODE_SESSION", "CLAUDE_CODE_MESSAGING_", "CLAUDE_TEAMS_", "WIN_AGENT_TEAMS_", "HERDR_",
        "CODEX_SANDBOX",
    ];

    static readonly HashSet<string> Dropped =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "CLAUDECODE", "CLAUDE_PID", "CLAUDE_EFFORT", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_CHILD_SESSION",
        "CLAUDE_CODE_EXECPATH", "CLAUDE_CODE_SSE_PORT", "CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS",
        "CODEX_THREAD_ID", "AGENT_NAME", "AGENT_PARENT_NAME", "AGENT_SESSION_ID",
        // An agent run by an earlier daemon carries its run marker; a daemon
        // that inherited it would be killed by its own orphan cleanup.
        "ATF_RUN_CORRELATION", "ATF_BOOTSTRAP_FILE", "ATF_DAEMON_LOG",
        // Agents' own MCP bridges must reach the daemon, never answer as a setup health check.
        "ATF_MCP_HEALTH_CHECK",
    ];

    internal static void Scrub(IDictionary<string, string?> environment)
    {
        foreach (var key in environment.Keys.ToArray())
        {
            if (!Keep(key))
            {
                environment.Remove(key);
            }
        }
    }

    internal static bool Keep(string key) => !Dropped.Contains(key)
        && !DroppedPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    internal static Dictionary<string, string?> Current()
    {
        var result = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        Scrub(result);
        return result;
    }
}
