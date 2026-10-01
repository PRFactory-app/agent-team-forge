namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>
/// A directory as the agent CLIs key it: realpath of the cwd, i.e. what getcwd() reports once a
/// process has chdir'd there (macOS /tmp is /private/tmp). A missing tail is kept as written, so a
/// deleted directory still has a path. Null when a link loop or an unreadable link prevents it.
/// </summary>
internal static class PhysicalPath
{
    public static string? Resolve(string fullPath) => Canonical(fullPath, 0);

    static string? Canonical(string full, int depth)
    {
        if (depth > 40) { return null; }
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
                // Traversal may be allowed without listing; a missing parent leaves nothing to list.
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            var next = Path.Combine(current, actual);
            // A link target may itself pass through links (macOS /tmp -> /private/tmp), so
            // canonicalize it again like realpath does.
            string? target;
            try { target = new DirectoryInfo(next).ResolveLinkTarget(returnFinalTarget: true)?.FullName; }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { target = null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
            if (target is not null)
            {
                if (Canonical(target, depth + 1) is not { } resolved) { return null; }
                current = resolved;
            }
            else { current = next; }
        }
        return current;
    }
}
