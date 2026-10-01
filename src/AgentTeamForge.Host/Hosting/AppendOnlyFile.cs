using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using AgentTeamForge.DAL.Files;
using Microsoft.Win32.SafeHandles;

namespace AgentTeamForge.Host.Hosting;

/// <summary>
/// A log file several processes append to (daemon.log: the daemon, its starter, a second daemon that
/// lost the lock). .NET's <see cref="FileMode.Append"/> only seeks to the end once, so each writer keeps
/// its own offset and overwrites the others. This opens the file append-only in the kernel (O_APPEND on
/// Unix, FILE_APPEND_DATA without FILE_WRITE_DATA on Windows) and writes every line with one call.
/// </summary>
internal sealed partial class AppendOnlyFile : IDisposable
{
    const int WriteOnly = 1;
    const uint AppendData = 0x4, Synchronize = 0x100000, ShareAll = 0x7, OpenAlways = 4, NormalAttributes = 0x80;

    readonly Lock _gate = new();

    AppendOnlyFile(SafeFileHandle handle) => Handle = handle;

    /// <summary>The append-only handle; a child given it as stdout/stderr appends the same way.</summary>
    public SafeFileHandle Handle { get; }

    public static AppendOnlyFile Open(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var created = CreateFile(path, AppendData | Synchronize, ShareAll, 0, OpenAlways, NormalAttributes, 0);
            return created.IsInvalid ? throw new IOException($"cannot open {path}", new Win32Exception(Marshal.GetLastPInvokeError()))
                : new AppendOnlyFile(created);
        }
        // Create owner-only first: open(2) with O_CREAT is variadic, which P/Invoke cannot call portably.
        using (new FileStream(path, PrivateFiles.Options(FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))) { }
        var flags = WriteOnly | (OperatingSystem.IsMacOS() ? 0x8 | 0x1000000 : 0x400 | Native.OpenCloseOnExec);
        var fd = Native.Open(path, flags);
        return fd < 0 ? throw new IOException($"cannot open {path}", new Win32Exception(Marshal.GetLastPInvokeError()))
            : new AppendOnlyFile(new SafeFileHandle(fd, ownsHandle: true));
    }

    /// <summary>Appends <paramref name="line"/> and a newline as one write, so it never interleaves with another writer's.</summary>
    public void WriteLine(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (_gate)
        {
            var added = false;
            try
            {
                Handle.DangerousAddRef(ref added);
                var handle = Handle.DangerousGetHandle();
                for (var offset = 0; offset < bytes.Length;)
                {
                    var written = OperatingSystem.IsWindows()
                        ? WriteFile(handle, bytes.AsSpan(offset), bytes.Length - offset, out var count, 0) ? count : -1
                        : (int)UnixWrite((int)handle, bytes.AsSpan(offset), bytes.Length - offset);
                    if (written < 0 && !OperatingSystem.IsWindows() && Marshal.GetLastPInvokeError() == 4) // EINTR
                    {
                        continue;
                    }
                    if (written <= 0)
                    {
                        throw new IOException("log write failed", new Win32Exception(Marshal.GetLastPInvokeError()));
                    }
                    offset += written;
                }
            }
            finally
            {
                if (added)
                {
                    Handle.DangerousRelease();
                }
            }
        }
    }

    public void Dispose() => Handle.Dispose();

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static partial nint UnixWrite(int fd, ReadOnlySpan<byte> buffer, nint count);

    [LibraryImport("kernel32.dll", EntryPoint = "WriteFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(nint handle, ReadOnlySpan<byte> buffer, int count, out int written, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security,
        uint creation, uint flags, nint template);
}
