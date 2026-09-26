using System.Diagnostics;
using System.Text;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>Linux restart cleanup for a direct child recorded by the old daemon.</summary>
public static class OrphanedBackendProcess
{
    const string Marker = "ATF_RUN_CORRELATION";

    public static void Mark(ProcessStartInfo info, string correlation) => info.Environment[Marker] = correlation;

    public static bool TryTerminate(int pid, string correlation)
    {
        if (!OperatingSystem.IsLinux() || pid <= 0)
        {
            return false;
        }

        try
        {
            // A PID alone may have been recycled. The random correlation was
            // placed in this child's environment before it started.
            var environment = File.ReadAllBytes($"/proc/{pid}/environ");
            var marker = Encoding.UTF8.GetBytes($"{Marker}={correlation}\0");
            var markerAt = environment.AsSpan().IndexOf(marker);
            if (markerAt < 0 || (markerAt > 0 && environment[markerAt - 1] != 0))
            {
                return false;
            }

            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
