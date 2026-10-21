using AgentTeamForge.Business.Features.Agents.Terminals;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>
/// The interactive Codex CLI (0.163) has no flag that ignores <c>$CODEX_HOME/config.toml</c>, unlike <c>codex exec --ignore-user-config</c>,
/// so its sandbox keys, project trust and MCP servers would still apply to a sandboxed TUI launch. ATF does not parse that file (TOML is
/// not worth a parser here): a config with any content makes the sandbox unavailable for the interactive Codex.
/// </summary>
public static class CodexConfigCheck
{
    public static bool HasUserConfig() =>
        HasContent(Path.Combine(CodexPaths.Home(Environment.GetEnvironmentVariable, Environment.CurrentDirectory), "config.toml"));

    /// <summary>True unless the file is absent, unreadable-as-absent, or only blank lines and whole-line comments.</summary>
    public static bool HasContent(string configPath)
    {
        if (!File.Exists(configPath)) { return false; }
        try { return File.ReadLines(configPath).Any(line => line.Trim() is { Length: > 0 } text && text[0] != '#'); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }
}
