using AgentTeamForge.DAL.Files;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Hosting;

/// <summary>Database integrity, private-mode policy for the state tree, and profile diagnostics.</summary>
public sealed class StateIntegrityTests
{
    static readonly TimeSpan BusyTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Intact_database_passes_verification()
    {
        using var dir = new TempStateDir();
        JobDatabase.Create(dir.File("jobs.db"), BusyTimeout);

        JobDatabase.Verify(dir.File("jobs.db"), BusyTimeout);
    }

    [Fact]
    public void Damaged_pages_are_reported_as_corrupt_with_sqlites_report()
    {
        using var dir = new TempStateDir();
        var path = dir.File("jobs.db");
        JobDatabase.Create(path, BusyTimeout);
        DamagePages(path);

        var ex = Assert.Throws<StorageException>(() => JobDatabase.Verify(path, BusyTimeout));

        Assert.Equal(StorageFailure.Corrupt, ex.Failure);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void A_file_that_is_not_a_database_is_reported_as_corrupt()
    {
        using var dir = new TempStateDir();
        var path = dir.File("jobs.db");
        JobDatabase.Create(path, BusyTimeout);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.Write(new byte[4096]);
        }

        Assert.Equal(StorageFailure.Corrupt, Assert.Throws<StorageException>(() => JobDatabase.Verify(path, BusyTimeout)).Failure);
    }

    [Fact]
    public void Created_database_is_owner_private_regardless_of_umask()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var dir = new TempStateDir();
        JobDatabase.Create(dir.File("jobs.db"), BusyTimeout);

        Assert.Equal(StateDirectory.PrivateFile, File.GetUnixFileMode(dir.File("jobs.db")));
    }

    [Fact]
    public void Loose_state_directory_error_names_path_expected_mode_and_fix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var dir = new TempStateDir();
        var state = NewState(dir);
        File.SetUnixFileMode(state, StateDirectory.PrivateDir | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var ex = Assert.Throws<StateDirectoryException>(() => StateDirectory.Open(state));

        Assert.Equal("state_dir_not_private", ex.Code);
        Assert.Contains(state, ex.Message, StringComparison.Ordinal);
        Assert.Contains("0755", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0700", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"chmod 700 \"{state}\"", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("jobs.db")]
    [InlineData("jobs.db-wal")]
    [InlineData("jobs.db-shm")]
    public void Group_readable_database_files_are_refused_with_the_fix(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var dir = new TempStateDir();
        var state = NewState(dir);
        var file = Path.Combine(state, name);
        if (!File.Exists(file))
        {
            using (new FileStream(file, PrivateFiles.Options(FileMode.CreateNew, FileAccess.Write))) { }
        }
        File.SetUnixFileMode(file, StateDirectory.PrivateFile | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var ex = Assert.Throws<StateDirectoryException>(() => StateDirectory.Open(state));

        Assert.Equal("private_file_unsafe", ex.Code);
        Assert.Contains("0644", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"chmod 600 \"{file}\"", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lenient_open_reports_loose_permissions_instead_of_refusing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var dir = new TempStateDir();
        var state = NewState(dir);
        File.SetUnixFileMode(state, StateDirectory.PrivateDir | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        File.SetUnixFileMode(Path.Combine(state, "jobs.db"), StateDirectory.PrivateFile | UnixFileMode.OtherRead);
        var warnings = new List<string>();

        var opened = StateDirectory.Open(state, warnings.Add);

        Assert.Equal(state, opened.Path);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, warning => warning.Contains("chmod 700", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("chmod 600", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{}", "missing required field(s): principal, team, agent, backend")]
    [InlineData("""{"principal":"p","team":"t","agent":"a"}""", "missing required field(s): backend")]
    [InlineData("{\"principal\":\"p\",\"backend\":", "invalid JSON")]
    [InlineData("null", "must contain a JSON object")]
    [InlineData("", "is empty")]
    [InlineData("""{"principal":"p","team":"t","agent":"a","backend":"fake","queue_limit":"many"}""", "wrong type")]
    public void Profile_errors_say_what_is_wrong(string content, string expected)
    {
        using var dir = new TempStateDir();
        var state = StateDirectory.Open(NewState(dir));
        using (var stream = new FileStream(state.ProfileFile, PrivateFiles.Options(FileMode.Create, FileAccess.Write)))
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes(content));
        }

        var ex = Assert.Throws<StateDirectoryException>(() => ProfileFile.Load(state));

        Assert.Equal("profile_invalid", ex.Code);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains(state.ProfileFile, ex.Message, StringComparison.Ordinal);
        if (expected != "invalid JSON")
        {
            Assert.DoesNotContain("invalid JSON", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Sidecars_deleted_while_open_checks_them_count_as_missing()
    {
        using var dir = new TempStateDir();
        var state = NewState(dir);
        var sidecars = new[] { Path.Combine(state, "jobs.db-wal"), Path.Combine(state, "jobs.db-shm") };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        // SQLite deletes -wal and -shm whenever the daemon's last connection closes.
        var churn = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var sidecar in sidecars)
                {
                    try
                    {
                        using (new FileStream(sidecar, PrivateFiles.Options(FileMode.OpenOrCreate, FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete))) { }
                        File.Delete(sidecar);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }, TestContext.Current.CancellationToken);

        while (!stop.IsCancellationRequested)
        {
            Assert.Equal(state, StateDirectory.Open(state).Path);
        }
        await churn;
    }

    static string NewState(TempStateDir dir)
    {
        var state = dir.File("state");
        Assert.Equal(0, InitCommand.Run(state, testProfile: true, queueLimit: null, maxRuntimeSeconds: null));
        return state;
    }

    /// <summary>Overwrites every page after the header page, as a torn or bit-rotted disk would.</summary>
    internal static void DamagePages(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
        Assert.True(stream.Length > 8192, $"test database too small: {stream.Length} bytes");
        stream.Position = 4096;
        var garbage = new byte[stream.Length - 4096];
        Array.Fill(garbage, (byte)0xA5);
        stream.Write(garbage);
        stream.Flush(flushToDisk: true);
    }
}
