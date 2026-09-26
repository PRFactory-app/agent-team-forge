namespace AgentTeamForge.Tests.Support;

/// <summary>
/// Owner-private disposable directory. Kept short because Unix socket paths
/// are limited to ~107 bytes.
/// </summary>
public sealed class TempStateDir : IDisposable
{
    public TempStateDir()
    {
        var root = Directory.Exists("/tmp") ? "/tmp" : System.IO.Path.GetTempPath();
        Path = System.IO.Path.Combine(root, "atf-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
