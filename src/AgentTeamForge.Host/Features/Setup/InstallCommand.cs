using System.Diagnostics;

namespace AgentTeamForge.Host.Features.Setup;

public static class InstallCommand
{
    public static int Uninstall(IReadOnlyDictionary<string, string> options)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable");
        var payload = new FileInfo(executable).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? executable;
        var script = Path.Combine(Path.GetDirectoryName(payload)!, "install.sh");
        if (!File.Exists(script))
        {
            Console.Error.WriteLine("error: bundled install.sh missing; use the release installer");
            return 1;
        }

        var start = new ProcessStartInfo("sh") { UseShellExecute = false };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--uninstall");
        if (options.ContainsKey("purge"))
        {
            start.ArgumentList.Add("--purge");
        }
        if (options.TryGetValue("state-dir", out var stateDir))
        {
            start.ArgumentList.Add("--state-dir");
            start.ArgumentList.Add(stateDir);
        }

        using var process = Process.Start(start);
        process?.WaitForExit();
        return process?.ExitCode ?? 1;
    }
}
