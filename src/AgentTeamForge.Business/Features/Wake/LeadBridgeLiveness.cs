using System.ComponentModel;
using System.Diagnostics;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.DAL.Features.Sessions;

namespace AgentTeamForge.Business.Features.Wake;

/// <summary>Positive bridge-process evidence. Missing metadata or inaccessible process state is unknown.
/// Linux PID absence proves death only in the recorded namespace with unrestricted /proc visibility.
/// No heartbeat timeout is used.</summary>
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
                    ? new(pid, (long)identity.StartTicks, ProcessScope(pid)) : null;
            }
            using var process = Process.GetProcessById(pid);
            return new(pid, process.StartTime.ToUniversalTime().Ticks, ProcessScope(pid));
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    public static bool? IsLive(BridgeProcessIdentity bridge)
    {
        if (bridge.Pid <= 0 || bridge.StartToken <= 0) { return null; }
        var scope = ProcessScope(Environment.ProcessId);
        if (scope is null || bridge.PidNamespace != scope) { return null; }
        try
        {
            using var process = Process.GetProcessById(bridge.Pid);
            if (process.HasExited) { return CanProveAbsence() ? false : null; }
            return Capture(bridge.Pid) is { } actual && actual.PidNamespace == scope
                ? actual.StartToken == bridge.StartToken : null;
        }
        catch (ArgumentException) { return CanProveAbsence() ? false : null; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    static string? ProcessScope(int pid)
    {
        // Windows and macOS have a single local PID space. The platform marker prevents
        // foreign/legacy metadata from being mistaken for a verified local identity.
        if (OperatingSystem.IsWindows()) { return "windows"; }
        if (OperatingSystem.IsMacOS()) { return "macos"; }
        if (!OperatingSystem.IsLinux()) { return null; }
        try
        {
            var path = pid == Environment.ProcessId ? "/proc/self/ns/pid" : $"/proc/{pid}/ns/pid";
            var target = new FileInfo(path).LinkTarget;
            return target is not null && target.StartsWith("pid:[", StringComparison.Ordinal)
                ? "linux:" + target : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    static bool CanProveAbsence()
    {
        if (!OperatingSystem.IsLinux()) { return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(); }
        try
        {
            // Both per-mount and superblock options can carry hidepid. Failure to find
            // or inspect the proc mount is unknown, never evidence that a PID died.
            var found = false;
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var parts = line.Split(" - ", StringSplitOptions.None);
                if (parts.Length != 2) { return false; }
                var mount = parts[0].Split(' ');
                var filesystem = parts[1].Split(' ');
                if (mount.Length < 6 || filesystem.Length < 3) { return false; }
                if (mount[4] != "/proc" || filesystem[0] != "proc") { continue; }
                found = true;
                if ((mount[5] + "," + filesystem[2]).Split(',').Any(option =>
                    option.StartsWith("hidepid", StringComparison.Ordinal) && option is not ("hidepid=0" or "hidepid=off")))
                {
                    return false;
                }
            }
            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
