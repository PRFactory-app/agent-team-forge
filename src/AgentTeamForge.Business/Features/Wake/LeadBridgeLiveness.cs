using System.ComponentModel;
using System.Diagnostics;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.DAL.Features.Sessions;

namespace AgentTeamForge.Business.Features.Wake;

/// <summary>Positive bridge-process evidence. Missing metadata or inaccessible process state is unknown;
/// PID absence, exit, or reuse proves the recorded bridge is gone. No heartbeat timeout is used.</summary>
public static class LeadBridgeLiveness
{
    public static BridgeProcessIdentity? Current() => Capture(Environment.ProcessId);

    public static BridgeProcessIdentity? Capture(int pid)
    {
        if (pid <= 0) { return null; }
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // .NET's derived Unix StartTime can differ across processes; use the existing
                // kernel token reader (/proc stat on Linux, kern.proc on macOS).
                var identity = new HerdrProcessRunner().Identity(pid);
                return identity is { StartTicks: > 0 and <= long.MaxValue }
                    ? new(pid, (long)identity.StartTicks) : null;
            }
            using var process = Process.GetProcessById(pid);
            return new(pid, process.StartTime.ToUniversalTime().Ticks);
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    public static bool? IsLive(BridgeProcessIdentity bridge)
    {
        if (bridge.Pid <= 0 || bridge.StartToken <= 0) { return null; }
        try
        {
            using var process = Process.GetProcessById(bridge.Pid);
            if (process.HasExited) { return false; }
            return Capture(bridge.Pid) is { } actual ? actual.StartToken == bridge.StartToken : null;
        }
        catch (ArgumentException) { return false; } // The recorded PID no longer exists.
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
