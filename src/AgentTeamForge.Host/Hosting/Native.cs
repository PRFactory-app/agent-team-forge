using System.Runtime.InteropServices;
using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

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
        _ when OperatingSystem.IsMacOS() => 0x100,
        Architecture.X64 => 0x20000,
        Architecture.Arm64 => 0x8000,
        _ => null,
    };

    [LibraryImport("libc", SetLastError = true)]
    public static partial int flock(nint fd, int operation);

    [LibraryImport("libc")]
    public static partial uint geteuid();

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Statx(int dirfd, string path, int flags, uint mask, out StatxBuffer buffer);

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int Fstat(nint fd, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "getpeereid", SetLastError = true)]
    public static partial int GetPeerEid(nint fd, out uint uid, out uint gid);

    [LibraryImport("libc", EntryPoint = "sysctlbyname", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SysctlByName(string name, [Out] byte[]? value, ref nuint size, nint replacement, nuint replacementSize);

    public static long? DarwinBootTime()
    {
        nuint size = 0;
        if (SysctlByName("kern.boottime", null, ref size, 0, 0) != 0 || size is < 8 or > 64)
        {
            return null;
        }

        var bytes = new byte[(int)size];
        return SysctlByName("kern.boottime", bytes, ref size, 0, 0) == 0
            ? BinaryPrimitives.ReadInt64LittleEndian(bytes) : null;
    }

    public static bool DarwinPrivateFile(SafeFileHandle handle, int maxBytes)
    {
        var buffer = new byte[256];
        if (Fstat(handle.DangerousGetHandle(), buffer) != 0)
        {
            return false;
        }
        // Darwin arm64 struct stat: mode at byte 4, UID at 16, size at 96.
        var mode = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(4));
        var uid = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16));
        var size = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(96));
        return (mode & FileTypeMask) == RegularFile && uid == geteuid()
            && ((UnixFileMode)(mode & ~FileTypeMask) & ~StateDirectory.PrivateFile) == 0
            && size is >= 0 && size <= maxBytes;
    }

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
