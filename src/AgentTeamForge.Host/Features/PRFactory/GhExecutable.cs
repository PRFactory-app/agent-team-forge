namespace AgentTeamForge.Host.Features.PRFactory;

/// <summary>
/// Finds the gh to run. A gh on PATH that is a script (e.g. a mise wrapper) can loop forever under the daemon,
/// which has no mise activation, so prefer the real binary from `mise which gh`; otherwise use PATH's gh.
/// </summary>
public static class GhExecutable
{
    static Task<string>? cached;

    public static Task<string> ResolveAsync(Func<ProcessSpec, CancellationToken, Task<ProcessResult>> run) =>
        cached ??= ResolveAsync(Environment.GetEnvironmentVariable("PATH"), run, CancellationToken.None);

    public static async Task<string> ResolveAsync(string? path, Func<ProcessSpec, CancellationToken, Task<ProcessResult>> run, CancellationToken ct)
    {
        var onPath = FindOnPath(path);
        if (onPath is null || IsElf(onPath)) { return onPath ?? "gh"; }
        try
        {
            var result = await run(new ProcessSpec("mise", ["which", "gh"], new Dictionary<string, string?> { ["MISE_QUIET"] = "1" }, null, TimeSpan.FromSeconds(10)), ct);
            var real = result.ExitCode == 0
                ? result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault()
                : null;
            if (real is not null && Path.IsPathRooted(real) && File.Exists(real) && IsElf(real)) { return real; }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException or IOException) { }
        return onPath;
    }

    static string? FindOnPath(string? path)
    {
        foreach (var dir in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? "gh.exe" : "gh");
            if (File.Exists(candidate)) { return candidate; }
        }
        return null;
    }

    static bool IsElf(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            Span<byte> magic = stackalloc byte[4];
            return stream.ReadAtLeast(magic, 4, false) == 4 && magic.SequenceEqual("\u007fELF"u8);
        }
        catch (IOException) { return false; }
    }
}
