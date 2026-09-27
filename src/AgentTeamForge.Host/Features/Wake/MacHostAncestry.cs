using System.Diagnostics;
using AgentTeamForge.Business.Features.Processes;

namespace AgentTeamForge.Host.Features.Wake;

internal static class MacHostAncestry
{
    internal static (int Pid, string Kind)? NearestHost()
    {
        if (!OperatingSystem.IsMacOS()) { return null; }
        try
        {
            var start = new ProcessStartInfo("/bin/ps") { UseShellExecute = false, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-axo", "pid=,ppid=,comm=" }) { start.ArgumentList.Add(arg); }
            using var process = NonInteractiveProcess.Start(start);
            if (process is null) { return null; }
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(3000)) { process.Kill(); return null; }
            if (process.ExitCode != 0) { return null; }
            return Resolve(Environment.ProcessId, output.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    internal static (int Pid, string Kind)? Resolve(int start, string output)
    {
        var rows = new Dictionary<int, WindowsHostAncestry.Row>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && int.TryParse(parts[0], out var pid) && int.TryParse(parts[1], out var parent))
            {
                rows[pid] = new(parent, parts[2]);
            }
        }
        return WindowsHostAncestry.Resolve(start, rows);
    }
}
