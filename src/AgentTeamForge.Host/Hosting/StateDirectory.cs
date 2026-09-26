using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentTeamForge.Host.Hosting;

/// <summary>
/// Local runtime/state directory. Linux uses 0700, descriptor-checked private
/// files, and peer UID checks. Windows init applies a current-user ACL to the
/// new tree; the named pipe also restricts connections to that user.
/// </summary>
public sealed class StateDirectory
{
    public const UnixFileMode PrivateDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    public const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Upper bound for operator.key (44 bytes as written by init) and profile.json
    /// (a few hundred bytes); generous headroom, but small enough that a replaced
    /// or corrupted file cannot make a client or daemon allocate unboundedly.
    /// </summary>
    public const int MaxPrivateFileBytes = 16 * 1024;

    StateDirectory(string path) => Path = path;

    public string Path { get; }

    public string Database => Combine("jobs.db");

    public string LockFile => Combine("daemon.lock");

    public string Socket => OperatingSystem.IsWindows() ? WindowsPipeName(Path) : Combine("daemon.sock");

    public string CredentialFile => Combine("operator.key");

    public string ProfileFile => Combine("profile.json");

    public string BarrierDir => Combine("barriers");

    public static StateDirectory Open(string path)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        var info = new DirectoryInfo(full);
        if (!info.Exists)
        {
            throw new StateDirectoryException("state_dir_missing");
        }

        if (info.LinkTarget is not null)
        {
            throw new StateDirectoryException("state_dir_symlink");
        }

        if (!OperatingSystem.IsWindows() && info.UnixFileMode != PrivateDir)
        {
            throw new StateDirectoryException("state_dir_not_private");
        }

        var state = new StateDirectory(full);
        if (!OperatingSystem.IsWindows() && state.Socket.Length > 100)
        {
            throw new StateDirectoryException("state_dir_path_too_long");
        }

        return state;
    }

    /// <summary>
    /// Reads an owner-private regular file of at most <see cref="MaxPrivateFileBytes"/>.
    /// The path is opened once, non-blocking and without following a final symlink;
    /// type, owner, mode and size are then checked on that open descriptor, so a
    /// path swap after the checks cannot redirect the read. FIFOs, devices,
    /// directories, sockets, symlinks, foreign or group/other-accessible files and
    /// oversize files fail closed as <c>private_file_unsafe</c> without blocking.
    /// Linux uses statx and O_NOFOLLOW; exercised on linux-x64. The Windows
    /// branch checks type, link and size, with native ACL validation still pending.
    /// </summary>
    public static byte[] ReadPrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new StateDirectoryException("private_file_missing");
            }

            if (info.LinkTarget is not null || info.Length > MaxPrivateFileBytes)
            {
                throw new StateDirectoryException("private_file_unsafe");
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaxPrivateFileBytes)
            {
                throw new StateDirectoryException("private_file_unsafe");
            }

            return bytes;
        }
        if (Native.OpenNoFollow is not { } noFollow)
        {
            throw new StateDirectoryException("private_file_unsafe");
        }

        var fd = Native.Open(path, Native.OpenReadOnly | Native.OpenNonBlocking | Native.OpenNoCtty | Native.OpenCloseOnExec | noFollow);
        if (fd < 0)
        {
            // ELOOP (final symlink), ENXIO (socket), EACCES, ... are all unsafe.
            throw new StateDirectoryException(Marshal.GetLastPInvokeError() == Native.ENOENT ? "private_file_missing" : "private_file_unsafe");
        }

        using var handle = new SafeFileHandle(fd, ownsHandle: true);
        const uint required = Native.StatxType | Native.StatxMode | Native.StatxUid | Native.StatxSize;
        if (Native.Statx(fd, "", Native.StatxEmptyPath, required, out var stat) != 0
            || (stat.Mask & required) != required
            || (stat.Mode & Native.FileTypeMask) != Native.RegularFile
            || stat.Uid != Native.geteuid()
            || ((UnixFileMode)(stat.Mode & ~Native.FileTypeMask) & ~PrivateFile) != 0
            || stat.Size > MaxPrivateFileBytes)
        {
            throw new StateDirectoryException("private_file_unsafe");
        }

        // Read one byte past the limit so growth after statx is also rejected.
        var buffer = new byte[MaxPrivateFileBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan(length), length);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length > MaxPrivateFileBytes)
        {
            throw new StateDirectoryException("private_file_unsafe");
        }

        return buffer[..length];
    }

    string Combine(string name) => System.IO.Path.Combine(Path, name);

    static string WindowsPipeName(string path) => "atf-" + Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToUpperInvariant())))[..24];
}

public sealed class StateDirectoryException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
