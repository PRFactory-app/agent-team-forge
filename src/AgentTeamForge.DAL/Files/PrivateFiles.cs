namespace AgentTeamForge.DAL.Files;

/// <summary>
/// Owner-only file and directory creation. Unix modes are applied only off Windows:
/// the .NET 11 runtime rejects any <see cref="FileStreamOptions.UnixCreateMode"/> assignment
/// on Windows (even null), where the state tree's inherited ACL applies instead.
/// </summary>
public static class PrivateFiles
{
    public const UnixFileMode Directory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    public const UnixFileMode File = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static FileStreamOptions Options(FileMode mode, FileAccess access, FileShare share = FileShare.None)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = File;
        }
        return options;
    }

    public static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(path);
        }
        else
        {
            System.IO.Directory.CreateDirectory(path, Directory);
        }
    }
}
