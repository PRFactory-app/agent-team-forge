namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>
/// Claude's config root as the launched agent sees it: CLAUDE_CONFIG_DIR (a relative value is
/// relative to the agent's working directory, as Claude itself resolves it), else ~/.claude.
/// </summary>
static class ClaudeConfigRoot
{
    public static string Resolve(Func<string, string?> environment, string workingDirectory)
    {
        if (environment("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured)
        {
            return Path.GetFullPath(configured, Path.GetFullPath(workingDirectory));
        }
        var home = environment("HOME") is { Length: > 0 } h && Path.IsPathFullyQualified(h)
            ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude");
    }
}
