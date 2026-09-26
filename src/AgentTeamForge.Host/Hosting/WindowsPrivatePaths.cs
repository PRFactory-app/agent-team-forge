using System.Diagnostics;
using System.Security.Principal;

namespace AgentTeamForge.Host.Hosting;

/// <summary>Give the current Windows user exclusive access to a new state tree.</summary>
internal static class WindowsPrivatePaths
{
    public static void Protect(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new StateDirectoryException("state_dir_owner_unknown");
        // Reset any explicit ACEs on a caller-supplied empty directory before
        // removing inherited ACEs. New state files then inherit only this user.
        RunIcacls(path, "/reset");
        RunIcacls(path, "/inheritance:r", "/grant:r", $"*{sid}:(OI)(CI)F");
    }

    static void RunIcacls(string path, params string[] args)
    {
        var info = new ProcessStartInfo("icacls.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(path);
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info) ?? throw new StateDirectoryException("state_dir_acl_failed");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            process.Kill();
            throw new StateDirectoryException("state_dir_acl_failed");
        }
        _ = output.GetAwaiter().GetResult();
        _ = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new StateDirectoryException("state_dir_acl_failed");
        }
    }
}
