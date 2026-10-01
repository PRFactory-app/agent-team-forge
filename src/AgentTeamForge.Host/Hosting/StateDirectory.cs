using AgentTeamForge.DAL.Files;
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
    public const UnixFileMode PrivateDir = PrivateFiles.Directory;
    public const UnixFileMode PrivateFile = PrivateFiles.File;

    /// <summary>0700 on Linux; on Windows the state tree's inherited current-user ACL applies.</summary>
    public static void CreatePrivateDirectory(string path) => PrivateFiles.CreateDirectory(path);

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

    /// <summary>
    /// Opens an existing state directory, refusing one that is not owner-private, together with
    /// its database files. With <paramref name="permissionWarning"/>, loose permissions are
    /// reported there instead of refused; only <c>atf stop</c> does this (see <c>SetupCommand.Stop</c>).
    /// </summary>
    public static StateDirectory Open(string path, Action<string>? permissionWarning = null)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        var info = new DirectoryInfo(full);
        if (!info.Exists)
        {
            throw new StateDirectoryException("state_dir_missing", $"{full} does not exist; run atf setup, or atf init --state-dir \"{full}\"");
        }

        if (info.LinkTarget is not null)
        {
            throw new StateDirectoryException("state_dir_symlink", $"{full} is a symbolic link; pass the real directory ({info.LinkTarget}) as --state-dir");
        }

        try
        {
            if (!OperatingSystem.IsWindows() && info.UnixFileMode != PrivateDir)
            {
                throw new StateDirectoryException("state_dir_not_private",
                    $"{full} has mode {Octal(info.UnixFileMode)}; it must be 0700 (owner only). Fix: chmod 700 \"{full}\"");
            }
            if (OperatingSystem.IsWindows())
            {
                try { WindowsPrivatePaths.ValidateDirectory(full); }
                catch (StateDirectoryException ex)
                {
                    throw new StateDirectoryException(ex.Code, $"{full} is not restricted to the current user. "
                        + $"Fix: icacls \"{full}\" /inheritance:r /grant:r \"%USERNAME%:(OI)(CI)F\" /T");
                }
            }
        }
        catch (StateDirectoryException ex) when (permissionWarning is not null)
        {
            permissionWarning(ex.Message);
        }

        if (SocketPathProblem(full) is { } problem)
        {
            throw new StateDirectoryException(PathTooLong, problem);
        }

        var state = new StateDirectory(full);
        // SQLite creates -wal and -shm with the database's mode, so all three follow one policy.
        foreach (var file in (string[])[state.Database, state.Database + "-wal", state.Database + "-shm"])
        {
            try { RequirePrivateFile(file); }
            catch (StateDirectoryException ex) when (ex.Code == "private_file_missing") { }
            catch (StateDirectoryException ex) when (permissionWarning is not null)
            {
                permissionWarning(ex.Message);
            }
        }
        return state;
    }

    public const string PathTooLong = "state_dir_path_too_long";

    /// <summary>
    /// Longest daemon socket path in UTF-8 bytes: sun_path is 108 bytes on Linux and 104 on
    /// macOS (and the BSDs), and holds the terminating NUL.
    /// </summary>
    public static int MaxSocketPathBytes => (OperatingSystem.IsLinux() ? 108 : 104) - 1;

    /// <summary>Why <paramref name="path"/> cannot hold the daemon socket, or null when it can (always on Windows).</summary>
    public static string? SocketPathProblem(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }
        var socket = System.IO.Path.Combine(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)), "daemon.sock");
        var bytes = Encoding.UTF8.GetByteCount(socket);
        return bytes <= MaxSocketPathBytes ? null
            : $"daemon socket path {socket} is {bytes} bytes; Unix sockets on this platform allow at most {MaxSocketPathBytes}. "
                + "Use a shorter --state-dir, a shorter HOME, or set XDG_STATE_HOME to a shorter directory.";
    }

    /// <summary>
    /// Reads an owner-private regular file of at most <see cref="MaxPrivateFileBytes"/>.
    /// The path is opened once, non-blocking and without following a final symlink;
    /// type, owner, mode and size are then checked on that open descriptor, so a
    /// path swap after the checks cannot redirect the read. FIFOs, devices,
    /// directories, sockets, symlinks, foreign or group/other-accessible files and
    /// oversize files fail closed as <c>private_file_unsafe</c> without blocking.
    /// Linux uses statx and O_NOFOLLOW; exercised on linux-x64. The Windows
    /// branch checks type, link, size and the ACL on the opened handle.
    /// </summary>
    public static byte[] ReadPrivateFile(string path)
    {
        using var handle = OpenPrivateFile(path, MaxPrivateFileBytes);
        // Read one byte past the limit so growth after the check is also rejected.
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
            throw Unsafe(path, MaxPrivateFileBytes);
        }

        return buffer[..length];
    }

    /// <summary>
    /// The same owner-private checks as <see cref="ReadPrivateFile"/>, without a size limit and
    /// without reading: for files such as <c>jobs.db</c> that SQLite opens itself.
    /// </summary>
    public static void RequirePrivateFile(string path)
    {
        using var handle = OpenPrivateFile(path, long.MaxValue);
    }

    static SafeFileHandle OpenPrivateFile(string path, long maxBytes)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw Missing(path);
            }

            if (info.LinkTarget is not null || info.Length > maxBytes)
            {
                throw Unsafe(path, maxBytes);
            }

            // daemon.lock and jobs.db stay open read/write by the live daemon; writers otherwise replace atomically.
            SafeFileHandle stream;
            try
            {
                stream = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            // SQLite deletes -wal and -shm when its last connection closes, so the file may vanish after the check.
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                throw Missing(path);
            }
            // As on Unix, a file that exists but cannot be opened is unsafe, not an unhandled crash.
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw Unsafe(path, maxBytes);
            }
            try
            {
                WindowsPrivatePaths.Validate(stream);
                if (RandomAccess.GetLength(stream) > maxBytes)
                {
                    throw Unsafe(path, maxBytes);
                }
                return stream;
            }
            catch (StateDirectoryException)
            {
                stream.Dispose();
                throw Unsafe(path, maxBytes);
            }
        }
        if (Native.OpenNoFollow is not { } noFollow)
        {
            throw Unsafe(path, maxBytes);
        }

        var flags = OperatingSystem.IsMacOS() ? noFollow | 0x1000000 | 4 : Native.OpenReadOnly | Native.OpenNonBlocking | Native.OpenNoCtty | Native.OpenCloseOnExec | noFollow;
        var fd = Native.Open(path, flags);
        if (fd < 0)
        {
            // ELOOP (final symlink), ENXIO (socket), EACCES, ... are all unsafe.
            throw Marshal.GetLastPInvokeError() == Native.ENOENT ? Missing(path) : Unsafe(path, maxBytes);
        }

        var handle = new SafeFileHandle(fd, ownsHandle: true);
        const uint required = Native.StatxType | Native.StatxMode | Native.StatxUid | Native.StatxSize;
        var safe = OperatingSystem.IsMacOS()
            ? Native.DarwinPrivateFile(handle, maxBytes)
            : Native.Statx(fd, "", Native.StatxEmptyPath, required, out var stat) == 0
                && (stat.Mask & required) == required
                && (stat.Mode & Native.FileTypeMask) == Native.RegularFile
                && stat.Uid == Native.geteuid()
                && ((UnixFileMode)(stat.Mode & ~Native.FileTypeMask) & ~PrivateFile) == 0
                && stat.Size <= (ulong)maxBytes;
        if (!safe)
        {
            handle.Dispose();
            throw Unsafe(path, maxBytes);
        }
        return handle;
    }

    static StateDirectoryException Missing(string path) =>
        new("private_file_missing", $"{path} is missing; run atf setup, or atf init on a new state directory");

    /// <summary>The refusal names the file, what was found where it can be told, and the fix.</summary>
    static StateDirectoryException Unsafe(string path, long maxBytes)
    {
        var limit = maxBytes == long.MaxValue ? "" : $" of at most {maxBytes} bytes";
        if (OperatingSystem.IsWindows())
        {
            return new("private_file_unsafe", $"{path} must be a regular file{limit} accessible only to the current user. "
                + $"Fix: icacls \"{path}\" /inheritance:r /grant:r \"%USERNAME%:F\"");
        }
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null)
            {
                return new("private_file_unsafe", $"{path} is a symbolic link; replace it with a regular file with mode 0600");
            }
            var mode = File.GetUnixFileMode(path);
            if ((mode & ~PrivateFile) != 0 && info.Exists)
            {
                return new("private_file_unsafe", $"{path} has mode {Octal(mode)}; it must be 0600 (owner read/write only). Fix: chmod 600 \"{path}\"");
            }
            if (info.Exists && info.Length > maxBytes)
            {
                return new("private_file_unsafe", $"{path} is {info.Length} bytes; the limit is {maxBytes}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new("private_file_unsafe", $"{path} must be a regular file{limit} owned by the current user with mode 0600");
    }

    static string Octal(UnixFileMode mode) => "0" + Convert.ToString((int)mode & 0xFFF, 8);

    string Combine(string name) => System.IO.Path.Combine(Path, name);

    static string WindowsPipeName(string path) => "atf-" + Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToUpperInvariant())))[..24];
}

public sealed class StateDirectoryException(string code, string? detail = null) : Exception(detail is null ? code : $"{code}: {detail}")
{
    public string Code { get; } = code;
}
