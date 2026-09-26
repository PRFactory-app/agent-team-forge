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
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
        "http_proxy", "https_proxy", "all_proxy", "no_proxy",
        "SSL_CERT_FILE", "SSL_CERT_DIR", "NODE_EXTRA_CA_CERTS", "REQUESTS_CA_BUNDLE", "CURL_CA_BUNDLE",
    ];

    static readonly string[] AllowedPrefixes = ["LC_", "MISE_", "CLAUDE_CODE_", "AWS_", "GOOGLE_", "GCP_", "GCLOUD_", "CLOUDSDK_"];

    // Parent Claude session state: its session/attendance markers, remote/bridge/host session
    // plumbing and SDK hooks. Provider and tuning CLAUDE_CODE_* configuration is kept.
    internal static readonly string[] IdentityPrefixes =
    [
        "CLAUDE_CODE_MESSAGING_", "CLAUDE_CODE_SESSION_", "CLAUDE_CODE_REMOTE", "CLAUDE_CODE_BRIDGE_",
        "CLAUDE_CODE_HOST_", "CLAUDE_CODE_SDK_", "CLAUDE_TEAMS_", "WIN_AGENT_TEAMS_", "HERDR_",
    ];

    internal static readonly string[] IdentityNames =
    [
        "CLAUDECODE", "CLAUDE_PID", "CLAUDE_EFFORT", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_EXECPATH",
        "CLAUDE_CODE_SSE_PORT", "CLAUDE_CODE_SESSION", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_PARENT_SESSION_ID",
        "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_CLOUD_SESSION_ID", "CLAUDE_CODE_CONTAINER_ID",
        "CLAUDE_CODE_PROJECTS_SESSION", "CLAUDE_CODE_RESUME_FROM_SESSION", "CLAUDE_CODE_TMUX_SESSION",
        "CLAUDE_CODE_TEAMMATE_COMMAND", "CLAUDE_CODE_MCP_SERVE_AUTH_TOKEN", "CLAUDE_CODE_ARTIFACT_FD",
        "CLAUDE_CODE_ARTIFACTS_API_TOKEN", "CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR",
        "CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR", "CLAUDE_CODE_GATEWAY_TOKEN_FILE_DESCRIPTOR",
        "CLAUDE_CODE_WEBSOCKET_AUTH_FILE_DESCRIPTOR", "AI_AGENT", "AGENT_NAME", "AGENT_PARENT_NAME", "AGENT_SESSION_ID",
        "CODEX_THREAD_ID", "ATF_EXTERNAL_ONLY", "ATF_RUN_CORRELATION", "ATF_BOOTSTRAP_FILE",
    ];

    static readonly HashSet<string> Identity = new(IdentityNames, StringComparer.OrdinalIgnoreCase);

    public static bool IsInheritedSessionContext(string name) =>
        Identity.Contains(name) || IdentityPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

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
