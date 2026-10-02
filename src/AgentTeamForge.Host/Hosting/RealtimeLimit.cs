using System.Runtime.InteropServices;

namespace AgentTeamForge.Host.Hosting;

/// <summary>
/// Claude Desktop starts its children with RLIMIT_RTTIME 0; the kernel SIGKILLs such a process the first time it
/// runs on the CPU without blocking, so a daemon that inherits it dies at random. A systemd service gets unlimited.
/// </summary>
internal static class RealtimeLimit
{
    const int RlimitRttime = 15;
    const ulong Infinity = ulong.MaxValue;

    /// <summary>The warning to log for these limits (microseconds), or null when the process cannot be killed by it.</summary>
    internal static string? Warning(ulong soft, ulong hard) =>
        soft == Infinity && hard == Infinity ? null
            : $"RLIMIT_RTTIME is {(soft == Infinity ? hard : soft)} us (inherited); the kernel may SIGKILL this daemon — start it via systemd service";

    /// <summary>Raises the soft limit to the hard one if it can, and returns the warning that still applies.</summary>
    internal static string? CheckAndRaise()
    {
        if (!OperatingSystem.IsLinux()) { return null; }
        try
        {
            Span<ulong> limits = stackalloc ulong[2];
            if (Native.getrlimit(RlimitRttime, ref MemoryMarshal.GetReference(limits)) != 0) { return null; }
            if (limits[0] != limits[1])
            {
                var raised = new[] { limits[1], limits[1] };
                if (Native.setrlimit(RlimitRttime, ref raised[0]) == 0) { limits[0] = limits[1]; }
            }
            return Warning(limits[0], limits[1]);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }
}
