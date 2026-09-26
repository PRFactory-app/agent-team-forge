using System.Runtime.InteropServices;

namespace AgentTeamForge.Host.Hosting;

static partial class Native
{
    public const int LockExclusive = 2;
    public const int LockNonBlocking = 4;

    public const int ENOENT = 2;
    public const int SigTerm = 15;
    public const int OpenReadOnly = 0;
    public const int OpenNoCtty = 0x100;
    public const int OpenNonBlocking = 0x800;
    public const int OpenCloseOnExec = 0x80000;

    public const int StatxEmptyPath = 0x1000;
    public const uint StatxType = 0x1;
    public const uint StatxMode = 0x2;
    public const uint StatxUid = 0x8;
    public const uint StatxSize = 0x200;
    public const ushort FileTypeMask = 0xF000;
    public const ushort RegularFile = 0x8000;

    /// <summary>
    /// O_NOFOLLOW is architecture-specific in the Linux uapi headers; unknown
    /// architectures get null so callers fail closed instead of following links.
    /// </summary>
    public static int? OpenNoFollow => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => 0x20000,
        Architecture.Arm64 => 0x8000,
        _ => null,
    };

    [LibraryImport("libc", SetLastError = true)]
    public static partial int flock(nint fd, int operation);

    [LibraryImport("libc")]
    public static partial uint geteuid();

    // A pidfd keeps the verified process identity stable if its numeric PID is reused.
    [LibraryImport("libc", EntryPoint = "pidfd_open", SetLastError = true)]
    public static partial int PidfdOpen(int pid, uint flags);

    [LibraryImport("libc", EntryPoint = "pidfd_send_signal", SetLastError = true)]
    public static partial int PidfdSendSignal(int pidfd, int signal, nint info, uint flags);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Statx(int dirfd, string path, int flags, uint mask, out StatxBuffer buffer);

    /// <summary>Linux struct statx: fixed 256-byte layout on every architecture.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    public struct StatxBuffer
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(40)] public ulong Size;
    }
}
