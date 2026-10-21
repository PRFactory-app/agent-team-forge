using AgentTeamForge.Business.Features.Agents.Terminals;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>
/// Codex 0.163 has no equivalent of Claude's <c>--setting-sources ""</c>: the user's <c>config.toml</c> still applies to every key
/// ATF does not override (ADR 0013, "Settings sources ATF controls"). This reads that file and reports whether it carries a key
/// that widens the sandbox, so a sandboxed Codex launch is not reported as enforced on top of it.
/// </summary>
public static class CodexConfigCheck
{
    static readonly string[] WideningKeys = ["sandbox_mode", "default_permissions", "writable_roots", "network_access"];
    static readonly string[] WideningTables = ["sandbox_workspace_write", "permissions"];

    public static bool WidensSandbox(string? cwd) =>
        WidensSandbox(cwd, Path.Combine(CodexPaths.Home(Environment.GetEnvironmentVariable, Environment.CurrentDirectory), "config.toml"));

    public static bool WidensSandbox(string? cwd, string configPath)
    {
        if (!File.Exists(configPath)) { return false; }
        string[] lines;
        try { lines = File.ReadAllLines(configPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        return Widens(lines, cwd);
    }

    internal static bool Widens(IEnumerable<string> lines, string? cwd)
    {
        string? trustedProject = null;
        var table = "";
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') { continue; }
            if (line[0] == '[')
            {
                table = line.Trim('[', ']', ' ');
                trustedProject = table.StartsWith("projects.", StringComparison.Ordinal) ? table["projects.".Length..].Trim('\'', '"') : null;
                if (WideningTables.Any(t => table == t || table.StartsWith(t + ".", StringComparison.Ordinal))) { return true; }
                continue;
            }
            var key = line.Split('=', 2)[0].Trim();
            if (table.Length == 0 && WideningKeys.Contains(key)) { return true; }
            if (trustedProject is not null && key == "trust_level" && line.Contains("trusted", StringComparison.Ordinal) && !line.Contains("untrusted", StringComparison.Ordinal)
                && Covers(trustedProject, cwd)) { return true; }
            // An inline `projects = { '<path>' = { trust_level = 'trusted' } }` is rare; any inline trust is treated as covering.
            if (table.Length == 0 && key == "projects" && line.Contains("trusted", StringComparison.Ordinal) && !line.Contains("untrusted", StringComparison.Ordinal)) { return true; }
        }
        return false;
    }

    static bool Covers(string project, string? cwd)
    {
        if (cwd is null) { return true; }
        var root = Path.TrimEndingDirectorySeparator(project);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
        return full == root || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
