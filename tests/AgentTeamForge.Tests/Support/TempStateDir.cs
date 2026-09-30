namespace AgentTeamForge.Tests.Support;

/// <summary>
/// Owner-private disposable directory. Kept short because Unix socket paths
/// are limited to ~107 bytes.
/// </summary>
public sealed class TempStateDir : IDisposable
{
    public TempStateDir()
    {
        var root = Environment.GetEnvironmentVariable("ATF_TEST_TMP_ROOT")
            ?? (!OperatingSystem.IsWindows() && Directory.Exists("/tmp") ? "/tmp" : System.IO.Path.GetTempPath());
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Test temp root does not exist: {root}");
        }

        Path = System.IO.Path.Combine(root, "atf-" + Guid.NewGuid().ToString("N")[..10]);
        AgentTeamForge.Host.Hosting.StateDirectory.CreatePrivateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        // The runner owns this parent and decides whether a failed run is kept.
        if (Environment.GetEnvironmentVariable("ATF_KEEP_TMP") == "1" &&
            Environment.GetEnvironmentVariable("ATF_TEST_TMP_ROOT") is { Length: > 0 })
        {
            return;
        }

        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
