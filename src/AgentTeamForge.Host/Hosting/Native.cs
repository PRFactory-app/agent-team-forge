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

    [LibraryImport("libc")]
    public static partial uint umask(uint mask);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Statx(int dirfd, string path, int flags, uint mask, out StatxBuffer buffer);

    // Darwin fstat with the 64-bit-inode struct stat: arm64 exports it as plain fstat, while plain
    // fstat on x86_64 is the legacy 32-bit-inode layout and the compiler binds fstat$INODE64 instead.
    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FstatArm64(nint fd, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static partial int FstatX64(nint fd, [Out] byte[] buffer);

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

    // The 64-bit-inode Darwin struct stat is identical on arm64 and x86_64 (offsetof-verified):
    // 144 bytes, st_mode at 4, st_uid at 16, st_size at 96.
    const int DarwinStatSize = 144, DarwinStatMode = 4, DarwinStatUid = 16, DarwinStatSizeField = 96;

    public static bool DarwinPrivateFile(SafeFileHandle handle, long maxBytes)
    {
        var buffer = new byte[DarwinStatSize];
        var fd = handle.DangerousGetHandle();
        var result = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => FstatArm64(fd, buffer),
            Architecture.X64 => FstatX64(fd, buffer),
            _ => -1,
        };
        if (result != 0)
        {
            return false;
        }
        var mode = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(DarwinStatMode));
        var uid = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(DarwinStatUid));
        var size = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(DarwinStatSizeField));
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
