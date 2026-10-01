using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

public static class CodexPaths
{
    public static string Home(Func<string, string?> environment, string daemonDirectory)
    {
        var home = environment("HOME") is { Length: > 0 } configuredHome && Path.IsPathFullyQualified(configuredHome)
            ? configuredHome : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configured = environment("CODEX_HOME");
        return Path.GetFullPath(configured is { Length: > 0 } ? configured : Path.Combine(home, ".codex"), daemonDirectory);
    }

    // An explicit CODEX_HOME must exist before Codex starts. With an unset home,
    // Codex can initialize its default directory itself on first use.
    public static string? LaunchHome(Func<string, string?> environment, string daemonDirectory)
    {
        var home = Home(environment, daemonDirectory);
        return environment("CODEX_HOME") is { Length: > 0 } || Directory.Exists(home) ? home : null;
    }

    internal static string TrustKey(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!Directory.Exists(full))
        {
            throw new BackendNotStartedException("Codex working directory does not exist");
        }
        var physical = PhysicalPath.Resolve(full)
            ?? throw new BackendNotStartedException("Codex working directory has too many symbolic links");
        var current = Path.TrimEndingDirectorySeparator(physical);
        return OperatingSystem.IsWindows() ? WindowsKey(current) : current;
    }

    internal static string WindowsKey(string path)
    {
        // Codex uses a conventional Windows path and ASCII lowercase for project keys.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) { path = @"\\" + path[8..]; }
        else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) { path = path[4..]; }
        return string.Create(path.Length, path, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;
            }
        });
    }
}
