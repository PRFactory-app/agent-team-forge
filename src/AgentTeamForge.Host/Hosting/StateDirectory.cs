namespace AgentTeamForge.Host.Hosting;

/// <summary>
/// Owner-private runtime/state directory. Rejects symlinks and any group/other
/// permission bits. Owner UID is not read directly (no portable .NET API);
/// the 0700 check plus peer-UID checks on the socket stand in for it.
/// </summary>
public sealed class StateDirectory
{
    public const UnixFileMode PrivateDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    public const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    StateDirectory(string path) => Path = path;

    public string Path { get; }

    public string Database => Combine("jobs.db");

    public string LockFile => Combine("daemon.lock");

    public string Socket => Combine("daemon.sock");

    public string CredentialFile => Combine("operator.key");

    public string ProfileFile => Combine("profile.json");

    public string BarrierDir => Combine("barriers");

    public static StateDirectory Open(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var info = new DirectoryInfo(full);
        if (!info.Exists)
        {
            throw new StateDirectoryException("state_dir_missing");
        }

        if (info.LinkTarget is not null)
        {
            throw new StateDirectoryException("state_dir_symlink");
        }

        if (info.UnixFileMode != PrivateDir)
        {
            throw new StateDirectoryException("state_dir_not_private");
        }

        var state = new StateDirectory(full);
        if (state.Socket.Length > 100)
        {
            throw new StateDirectoryException("state_dir_path_too_long");
        }

        return state;
    }

    /// <summary>Reads a private regular file, rejecting symlinks or group/other access.</summary>
    public static byte[] ReadPrivateFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new StateDirectoryException("private_file_missing");
        }

        if (info.LinkTarget is not null || (info.UnixFileMode & ~PrivateFile) != 0)
        {
            throw new StateDirectoryException("private_file_unsafe");
        }

        return File.ReadAllBytes(path);
    }

    string Combine(string name) => System.IO.Path.Combine(Path, name);
}

public sealed class StateDirectoryException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
