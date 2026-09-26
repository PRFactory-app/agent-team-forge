using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>Linux pidfd helpers: pin a process so a recycled PID is never signalled.</summary>
public static partial class Pidfd
{
    // Same numbers on x64 and arm64 (unified syscall table since Linux 5.3).
    const long SysPidfdSendSignal = 424;
    const long SysPidfdOpen = 434;

    public static SafeFileHandle? Open(int pid)
    {
        var fd = SyscallPidfdOpen(SysPidfdOpen, pid, 0);
        return fd < 0 ? null : new SafeFileHandle(fd, ownsHandle: true);
    }

    public static bool Signal(SafeFileHandle pidfd, int signal)
    {
        var added = false;
        try
        {
            pidfd.DangerousAddRef(ref added);
            return SyscallPidfdSendSignal(SysPidfdSendSignal, (int)pidfd.DangerousGetHandle(), signal, 0, 0) == 0;
        }
        finally
        {
            if (added)
            {
                pidfd.DangerousRelease();
            }
        }
    }

    [LibraryImport("libc", EntryPoint = "syscall")]
    private static partial nint SyscallPidfdOpen(long number, int pid, uint flags);

    [LibraryImport("libc", EntryPoint = "syscall")]
    private static partial long SyscallPidfdSendSignal(long number, int pidfd, int signal, nint info, uint flags);
}
