using System.Diagnostics;

namespace AgentTeamForge.Host.Features.Setup;

public static class InstallCommand
{
    public static int Uninstall(IReadOnlyDictionary<string, string> options)
    {
        if (options.ContainsKey("teardown-only"))
        {
            var home = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var binary = OperatingSystem.IsWindows()
                ? Path.Combine(home, ".local", "share", "agentteamforge", "bin", "atf.exe")
                : Path.Combine(home, ".local", "bin", "atf");
            var teardownState = SetupCommand.ResolveStateDir(options);
            return ClientSetup.Teardown(binary, teardownState, home, SetupCommand.RunCommand)
                && LoginAutostart.RemoveOwned(home, binary, teardownState, SetupCommand.RunCommand) ? 0 : 1;
        }
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable");
        var payload = new FileInfo(executable).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? executable;
        var windows = OperatingSystem.IsWindows();
        var scriptName = windows ? "install.ps1" : "install.sh";
        var script = Path.Combine(Path.GetDirectoryName(payload)!, scriptName);
        if (!File.Exists(script))
        {
            Console.Error.WriteLine($"error: bundled {scriptName} missing; use the release installer");
            return 1;
        }

        var start = new ProcessStartInfo(windows ? "powershell.exe" : "sh") { UseShellExecute = false };
        if (windows)
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
        }
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(windows ? "-Uninstall" : "--uninstall");
        if (windows)
        {
            // The current process holds atf.exe open until this command returns.
            start.ArgumentList.Add("-ParentPid");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.CreateNoWindow = true;
        }
        if (options.ContainsKey("purge"))
        {
            start.ArgumentList.Add(windows ? "-Purge" : "--purge");
        }
        if (options.TryGetValue("state-dir", out var stateDir))
        {
            start.ArgumentList.Add(windows ? "-StateDir" : "--state-dir");
            start.ArgumentList.Add(stateDir);
        }

        using var process = Process.Start(start);
        if (windows)
        {
            if (process is null)
            {
                return 1;
            }
            Console.WriteLine("Uninstall started; binary cleanup finishes after this command exits.");
            return 0;
        }
        process?.WaitForExit();
        return process?.ExitCode ?? 1;
    }
}
