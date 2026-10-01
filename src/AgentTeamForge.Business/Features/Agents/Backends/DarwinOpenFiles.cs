using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>Darwin has no /proc/PID/fd: libproc lists a process's descriptors and the paths of its open files.</summary>
public static partial class DarwinProcess
{
    // struct proc_fdinfo is {int32 proc_fd; uint32 proc_fdtype}; struct vnode_fdinfowithpath is 1200 bytes
    // with vip_path (MAXPATHLEN) at 176. Measured with clang on arm64; the layout is the same on x86_64.
    const int ProcPidListFds = 1, ProcPidFdVnodePathInfo = 2, FdTypeVnode = 1;
    const int FdInfoSize = 8, VnodePathInfoSize = 1200, VnodePathOffset = 176, FdPathMax = 1024;
    const int FdListAttempts = 4, Ebadf = 9;

    /// <summary>
    /// Paths of the files <paramref name="pid"/> holds open. Throws <see cref="IOException"/> when the
    /// process or any of its descriptors cannot be read, so a partial list is never returned.
    /// </summary>
    public static IReadOnlyList<string> OpenFiles(int pid)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("libproc is Darwin-only");
        }

        var raw = DescriptorTable(pid, (buffer, size) => ListFds(pid, ProcPidListFds, 0, buffer, size));
        var paths = new List<string>();
        var info = new byte[VnodePathInfoSize];
        for (var offset = 0; offset + FdInfoSize <= raw.Length; offset += FdInfoSize)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset + 4)) != FdTypeVnode)
            {
                continue;
            }

            var fd = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(offset));
            if (FdInfo(pid, fd, ProcPidFdVnodePathInfo, info, VnodePathInfoSize) != VnodePathInfoSize)
            {
                // A descriptor closed since the listing is no longer open, so it is not part of the answer.
                if (Marshal.GetLastPInvokeError() == Ebadf)
                {
                    continue;
                }
                throw new IOException($"descriptor {fd} of PID {pid} is unreadable");
            }

            var path = info.AsSpan(VnodePathOffset, FdPathMax);
            var end = path.IndexOf((byte)0);
            paths.Add(Encoding.UTF8.GetString(end < 0 ? path : path[..end]));
        }
        return paths;
    }

    /// <summary>
    /// The raw proc_fdinfo table from <paramref name="list"/> (proc_pidinfo's buffer and size arguments). A read
    /// that fills the buffer may have been cut short by descriptors opened since the size probe, so it is retried
    /// with a larger buffer; a table that keeps filling it throws rather than being returned truncated.
    /// </summary>
    internal static byte[] DescriptorTable(int pid, Func<byte[]?, int, int> list)
    {
        var size = list(null, 0);
        if (size < 0 || size == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            throw new IOException($"descriptors of PID {pid} are unreadable");
        }
        if (size == 0)
        {
            return [];
        }

        // Headroom for descriptors opened between the size probe and the read, doubled on every full read.
        var capacity = size + (32 * FdInfoSize);
        for (var attempt = 0; attempt < FdListAttempts; attempt++, capacity *= 2)
        {
            var raw = new byte[capacity];
            var read = list(raw, raw.Length);
            if (read <= 0)
            {
                throw new IOException($"descriptors of PID {pid} are unreadable");
            }
            if (read < raw.Length)
            {
                return raw[..read];
            }
        }
        throw new IOException($"descriptors of PID {pid} kept growing during the probe");
    }

    [LibraryImport("libc", EntryPoint = "proc_pidinfo", SetLastError = true)]
    private static partial int ListFds(int pid, int flavor, ulong argument, [Out] byte[]? buffer, int size);

    [LibraryImport("libc", EntryPoint = "proc_pidfdinfo", SetLastError = true)]
    private static partial int FdInfo(int pid, int fd, int flavor, [Out] byte[] buffer, int size);
}
