using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Tests.Support;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.Tests.Hosting;

public sealed class StartupBackupTests
{
    static readonly TimeSpan BusyTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void New_boot_creates_consistent_backup_of_wal_database()
    {
        using var dir = new TempStateDir();
        var state = CreateState(dir);
        using var writer = new SqliteConnection($"Data Source={state.Database}");
        writer.Open();
        using (var command = writer.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE backup_probe(value TEXT); INSERT INTO backup_probe VALUES ('committed');";
            command.ExecuteNonQuery();
        }

        StartupBackup.Run(state, BusyTimeout, _ => { }, () => "boot-one");

        var backup = Assert.Single(Backups(state));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(StateDirectory.PrivateDir, File.GetUnixFileMode(Path.Combine(state.Path, "backups")));
            Assert.Equal(StateDirectory.PrivateFile, File.GetUnixFileMode(backup));
            Assert.Equal(StateDirectory.PrivateFile, File.GetUnixFileMode(Path.Combine(state.Path, "backups", "boot-id")));
        }
        using var copy = new SqliteConnection($"Data Source={backup};Mode=ReadOnly");
        copy.Open();
        using var query = copy.CreateCommand();
        query.CommandText = "SELECT value FROM backup_probe";
        Assert.Equal("committed", query.ExecuteScalar());
    }

    [Fact]
    public void Same_boot_does_not_create_another_backup()
    {
        using var dir = new TempStateDir();
        var state = CreateState(dir);
        StartupBackup.Run(state, BusyTimeout, _ => { }, () => "boot-one");
        StartupBackup.Run(state, BusyTimeout, _ => { }, () => "boot-one");

        Assert.Single(Backups(state));
    }

    [Fact]
    public void Rotation_keeps_two_newest_backups()
    {
        using var dir = new TempStateDir();
        var state = CreateState(dir);
        StartupBackup.Run(state, BusyTimeout, _ => { }, () => "boot-one");
        var first = Assert.Single(Backups(state));
        StartupBackup.Run(state, BusyTimeout, _ => { }, () => "boot-two");
        var second = Assert.Single(Backups(state), path => path != first);
        StartupBackup.Run(state, BusyTimeout, _ => { }, () => "boot-three");

        var remaining = Backups(state);
        Assert.Equal(2, remaining.Length);
        Assert.DoesNotContain(first, remaining);
        Assert.Contains(second, remaining);
    }

    [Fact]
    public void Damaged_database_never_rotates_out_a_good_backup()
    {
        using var dir = new TempStateDir();
        var state = CreateState(dir);
        StartupBackup.Run(state, BusyTimeout, _ => { }, () => "boot-one");
        StartupBackup.Run(state, BusyTimeout, _ => { }, () => "boot-two");
        var good = Backups(state).Order(StringComparer.Ordinal).ToArray();
        StateIntegrityTests.DamagePages(state.Database);
        var log = new List<string>();

        StartupBackup.Run(state, BusyTimeout, log.Add, () => "boot-three");
        StartupBackup.Run(state, BusyTimeout, log.Add, () => "boot-four");

        Assert.Equal(good, Backups(state).Order(StringComparer.Ordinal).ToArray());
        Assert.All(good, backup => JobDatabase.Verify(backup, BusyTimeout));
        Assert.Equal(2, log.Count(line => line.StartsWith("backup failed:", StringComparison.Ordinal)));
        Assert.Empty(Directory.GetFiles(Path.Combine(state.Path, "backups"), "*-wal"));
    }

    static StateDirectory CreateState(TempStateDir dir)
    {
        var state = StateDirectory.Open(dir.Path);
        JobDatabase.Create(state.Database, BusyTimeout);
        return state;
    }

    static string[] Backups(StateDirectory state) =>
        Directory.GetFiles(Path.Combine(state.Path, "backups"), "jobs-*.db");
}
