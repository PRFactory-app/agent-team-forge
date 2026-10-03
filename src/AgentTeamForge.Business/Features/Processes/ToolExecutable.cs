using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Processes;

/// <summary>
/// Finds the executable to run for a CLI name. A mise wrapper on PATH (<c>exec mise x "X" -- "X"</c>) loops forever
/// when the caller has no mise activation (systemd daemon, install unit): <c>mise x</c> finds the wrapper again.
/// When the PATH hit is such a wrapper, ask <c>mise which</c> and use its answer if that is a native binary, or a script
/// that is not itself a mise wrapper (cursor-agent ships a bash launcher). Otherwise keep the PATH hit. Nothing is
/// cached: a lookup is cheap, and a later call retries mise after a failure.
/// </summary>
public static partial class ToolExecutable
{
    static readonly TimeSpan MiseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The executable for <paramref name="name"/> on this process's PATH; the name itself on Windows or when absent.</summary>
    public static string Resolve(string name) =>
        OperatingSystem.IsWindows() || name.Contains('/') ? name : Resolve(name, Environment.GetEnvironmentVariable("PATH"), MiseWhich);

    public static string Resolve(string name, string? path, Func<string, string?> miseWhich)
    {
        var onPath = FindOnPath(name, path);
        if (onPath is null || !IsMiseWrapper(onPath)) { return onPath ?? name; }
        string? real = null;
        try
        {
            real = miseWhich(name)?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException or IOException or InvalidOperationException) { }
        return real is not null && Path.IsPathRooted(real) && Head(real).Length > 0 && !IsMiseWrapper(real) ? real : onPath;
    }

    /// <summary>Stdout of <c>mise which name</c>, or null when mise is missing, fails or does not answer within 5 s.</summary>
    static string? MiseWhich(string name)
    {
        var info = new ProcessStartInfo("mise") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = "/" };
        info.ArgumentList.Add("which");
        info.ArgumentList.Add(name);
        info.Environment["MISE_QUIET"] = "1";
        using var process = NonInteractiveProcess.Start(info);
        if (process is null) { return null; }
        var stdout = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(MiseTimeout))
        {
            OwnedProcessTermination.Kill(process);
            return null;
        }
        return process.ExitCode == 0 ? stdout.GetAwaiter().GetResult() : null;
    }

    static string? FindOnPath(string name, string? path)
    {
        foreach (var dir in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate) && (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
            {
                return candidate;
            }
        }
        return null;
    }

    // File reads follow symlinks, so a link to a script or to the wrapper reads as that script.
    static bool IsScript(string file) => Head(file) is { Length: >= 2 } head && head[0] == '#' && head[1] == '!';

    static bool IsMiseWrapper(string file) => IsScript(file) && MiseExec().IsMatch(Encoding.UTF8.GetString(Head(file)));

    static byte[] Head(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var buffer = new byte[4096];
            return buffer[..stream.ReadAtLeast(buffer, buffer.Length, false)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    [GeneratedRegex(@"\bmise\s+(x|exec)\b")]
    private static partial Regex MiseExec();
}
