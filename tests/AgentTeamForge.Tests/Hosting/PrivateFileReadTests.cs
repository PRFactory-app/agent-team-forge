using System.Diagnostics;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Hosting;

/// <summary>
/// StateDirectory.ReadPrivateFile must accept only an owner-private regular file
/// of bounded size and must fail closed, never hang, on anything else.
/// </summary>
public sealed class PrivateFileReadTests : IDisposable
{
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    readonly TempStateDir _dir = new();

    [Fact]
    public async Task Owner_private_regular_file_up_to_the_limit_is_read_exactly()
    {
        var content = new byte[StateDirectory.MaxPrivateFileBytes];
        Random.Shared.NextBytes(content);
        var path = Private("operator.key", content);

        Assert.Equal(content, await ReadBounded(path));
    }

    [Fact]
    public async Task Oversize_file_is_rejected()
    {
        var path = Private("operator.key", new byte[StateDirectory.MaxPrivateFileBytes + 1]);

        Assert.Equal("private_file_unsafe", await RejectCode(path));
    }

    [Fact]
    public async Task Missing_file_is_reported_missing()
    {
        Assert.Equal("private_file_missing", await RejectCode(_dir.File("operator.key")));
    }

    [Fact]
    public async Task Group_readable_file_is_rejected()
    {
        var path = Private("operator.key", "k"u8.ToArray());
        File.SetUnixFileMode(path, StateDirectory.PrivateFile | UnixFileMode.GroupRead);

        Assert.Equal("private_file_unsafe", await RejectCode(path));
    }

    [Fact]
    public async Task Symlink_to_a_private_file_is_rejected()
    {
        var target = Private("real.key", "k"u8.ToArray());
        var link = _dir.File("operator.key");
        File.CreateSymbolicLink(link, target);

        Assert.Equal("private_file_unsafe", await RejectCode(link));
    }

    [Fact]
    public async Task Directory_is_rejected()
    {
        var path = _dir.File("operator.key");
        Directory.CreateDirectory(path, StateDirectory.PrivateDir);

        Assert.Equal("private_file_unsafe", await RejectCode(path));
    }

    [Fact]
    public async Task Fifo_without_a_writer_is_rejected_without_blocking()
    {
        var path = _dir.File("operator.key");
        MakeFifo(path);
        try
        {
            Assert.Equal("private_file_unsafe", await RejectCode(path));
        }
        finally
        {
            ReleaseBlockedReader(path);
        }
    }

    [Fact]
    public async Task Character_device_is_rejected()
    {
        // Not owner-private, so this is fail-closed coverage only; the file-type
        // check itself is exercised by the FIFO and directory cases.
        Assert.Equal("private_file_unsafe", await RejectCode("/dev/zero"));
    }

    public void Dispose() => _dir.Dispose();

    string Private(string name, byte[] content)
    {
        var path = _dir.File(name);
        File.WriteAllBytes(path, content);
        File.SetUnixFileMode(path, StateDirectory.PrivateFile);
        return path;
    }

    // A regressed read may block forever inside open/read; bound it so the
    // test fails instead of hanging the suite.
    static Task<byte[]> ReadBounded(string path) =>
        Task.Run(() => StateDirectory.ReadPrivateFile(path)).WaitAsync(Deadline, TestContext.Current.CancellationToken);

    static async Task<string> RejectCode(string path) =>
        (await Assert.ThrowsAsync<StateDirectoryException>(() => ReadBounded(path))).Code;

    static void MakeFifo(string path)
    {
        using var mkfifo = Process.Start(new ProcessStartInfo("mkfifo", ["-m", "600", path]) { UseShellExecute = false })!;
        Assert.True(mkfifo.WaitForExit(Deadline), "mkfifo timed out");
        Assert.Equal(0, mkfifo.ExitCode);
    }

    // If a regression left a reader blocked in open(2), a short-lived writer
    // lets it complete so no thread outlives the test. The writer itself blocks
    // when there is no reader, so it is killed after a bounded wait.
    static void ReleaseBlockedReader(string path)
    {
        using var writer = Process.Start(new ProcessStartInfo("sh", ["-c", "exec 3>\"$1\"", "sh", path]) { UseShellExecute = false })!;
        if (!writer.WaitForExit(TimeSpan.FromSeconds(1)))
        {
            writer.Kill();
            writer.WaitForExit(Deadline);
        }
    }
}
