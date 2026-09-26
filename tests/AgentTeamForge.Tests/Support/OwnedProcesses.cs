using System.Diagnostics;

namespace AgentTeamForge.Tests.Support;

/// <summary>
/// Tracks only processes this test started (by handle) so cleanup never
/// touches anything it cannot prove it owns.
/// </summary>
public sealed class OwnedProcesses : IDisposable
{
    readonly List<Process> _owned = [];

    public Process Start(ProcessStartInfo info)
    {
        var process = Process.Start(info) ?? throw new InvalidOperationException("process did not start");
        _owned.Add(process);
        return process;
    }

    public static void KillAbruptly(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: false);
        }

        process.WaitForExit(10_000);
    }

    public void Dispose()
    {
        foreach (var process in _owned)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: false);
                    process.WaitForExit(5_000);
                }
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
        }
    }
}
