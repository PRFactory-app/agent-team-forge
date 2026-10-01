namespace AgentTeamForge.Host.Features.Setup;

/// <summary>
/// Link-resolved path comparison. The OS reports some paths already resolved
/// (macOS ProcessPath is /private/tmp/... for a HOME of /tmp/...), so comparing
/// them with paths built from HOME or user input must resolve both sides first.
/// Paths that are registered or printed keep their written form.
/// </summary>
internal static class CanonicalPath
{
    internal static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Like realpath: resolves a link in every existing component; a missing tail stays as written.</summary>
    internal static string Resolve(string path) => Path.TrimEndingDirectorySeparator(Resolve(Path.GetFullPath(path), 0));

    internal static bool Same(string left, string right) => Resolve(left).Equals(Resolve(right), Comparison);

    internal static bool Within(string path, string root)
    {
        var full = Resolve(path);
        var parent = Resolve(root);
        return full.Equals(parent, Comparison)
            || full.StartsWith(parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar, Comparison);
    }

    static string Resolve(string full, int depth)
    {
        // A link cycle is reported by the operation that uses the path; compare it as written.
        if (depth > 40)
        {
            return full;
        }
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            // Resolve against the already canonical parent, so a relative target's ".." is the real parent.
            var next = Path.Combine(current, part);
            string? target = null;
            try
            {
                target = (Directory.Exists(next)
                    ? Directory.ResolveLinkTarget(next, returnFinalTarget: true)
                    : File.ResolveLinkTarget(next, returnFinalTarget: true))?.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            current = target is null ? next : Resolve(target, depth + 1);
        }
        return current;
    }
}
