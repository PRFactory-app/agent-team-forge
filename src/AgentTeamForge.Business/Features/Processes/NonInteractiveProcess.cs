using System.Diagnostics;

namespace AgentTeamForge.Business.Features.Processes;

/// <summary>Starts helper processes with stdin disconnected from the caller.</summary>
public static class NonInteractiveProcess
{
    public static Process? Start(ProcessStartInfo startInfo)
    {
        startInfo.RedirectStandardInput = true;
        OwnProcessGroup(startInfo);
        var process = Process.Start(startInfo);
        process?.StandardInput.Close();
        return process;
    }

    /// <summary>
    /// On Linux, runs the process behind <c>setsid</c> (exec in place, same PID) so it leads its own session and process
    /// group: a group-directed kill from or at a helper can never reach the daemon. Left unchanged when the target or
    /// setsid cannot be resolved, so a missing executable still fails at start as before.
    /// </summary>
    public static void OwnProcessGroup(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsLinux() || startInfo.FileName == "setsid"
            || Resolve(startInfo.FileName, startInfo) is not { } target || Resolve("setsid", startInfo) is not { } setsid)
        {
            return;
        }

        var args = startInfo.ArgumentList.ToList();
        startInfo.ArgumentList.Clear();
        startInfo.ArgumentList.Add(target);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        startInfo.FileName = setsid;
    }

    static string? Resolve(string file, ProcessStartInfo startInfo)
    {
        if (file.Length == 0 || startInfo.Arguments.Length > 0)
        {
            return null; // Raw Arguments string: leave untouched.
        }

        if (file.Contains('/'))
        {
            var path = Path.GetFullPath(file, startInfo.WorkingDirectory is { Length: > 0 } cwd ? cwd : Environment.CurrentDirectory);
            return IsExecutable(path) ? path : null;
        }

        var searchPath = startInfo.Environment.TryGetValue("PATH", out var value) ? value : Environment.GetEnvironmentVariable("PATH");
        foreach (var dir in (searchPath ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, file);
            if (IsExecutable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    static bool IsExecutable(string path) =>
        File.Exists(path) && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
}
