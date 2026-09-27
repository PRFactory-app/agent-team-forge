using System.Diagnostics;

namespace AgentTeamForge.Business.Features.Processes;

/// <summary>Starts helper processes with stdin disconnected from the caller.</summary>
public static class NonInteractiveProcess
{
    public static Process? Start(ProcessStartInfo startInfo)
    {
        startInfo.RedirectStandardInput = true;
        var process = Process.Start(startInfo);
        process?.StandardInput.Close();
        return process;
    }
}
