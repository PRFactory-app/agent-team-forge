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

    internal static string TrustKey(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!Directory.Exists(full))
        {
            throw new BackendNotStartedException("Codex working directory does not exist");
        }
        // Resolve every component: ResolveLinkTarget on the final directory alone misses
        // a symlink or junction in one of its parents.
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            // On a case-insensitive macOS volume, canonicalize the on-disk spelling
            // without changing case on case-sensitive volumes.
            var actual = part;
            if (OperatingSystem.IsMacOS())
            {
                try
                {
                    var entries = Directory.EnumerateFileSystemEntries(current).Select(Path.GetFileName).ToArray();
                    actual = entries.FirstOrDefault(name => string.Equals(name, part, StringComparison.Ordinal))
                        ?? entries.FirstOrDefault(name => string.Equals(name, part, StringComparison.OrdinalIgnoreCase)) ?? part;
                }
                catch (UnauthorizedAccessException) { /* Traversal may be allowed without listing. */ }
            }
            var next = Path.Combine(current, actual);
            current = new DirectoryInfo(next).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? next;
        }
        current = Path.TrimEndingDirectorySeparator(current);
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
