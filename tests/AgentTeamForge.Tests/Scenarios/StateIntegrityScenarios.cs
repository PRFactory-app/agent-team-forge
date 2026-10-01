using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Tests.Hosting;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Scenarios;

[Trait("Category", "Scenario")]
public sealed class StateIntegrityScenarios
{
    [Fact]
    public async Task Corrupt_database_is_refused_before_ready_keeps_its_backup_and_restores()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        await rig.StartDaemonAsync();
        var accepted = await rig.SubmitAsync("before-damage", "kept in the backup");
        Assert.True(accepted.Ok, accepted.Error);
        await rig.WaitForStatusAsync(accepted.Job!.JobId, JobStatus.Completed);
        Assert.Equal(0, (await rig.RunToExitAsync(["stop", "--state-dir", rig.StateDir])).Exit);
        // A backup taken now holds the job; the next start is treated as a new boot.
        var backups = Path.Combine(rig.StateDir, "backups");
        File.Delete(Path.Combine(backups, "boot-id"));
        await rig.StartDaemonAsync();
        Assert.Equal(0, (await rig.RunToExitAsync(["stop", "--state-dir", rig.StateDir])).Exit);
        var good = Directory.GetFiles(backups, "jobs-*.db").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(2, good.Length);
        var database = Path.Combine(rig.StateDir, "jobs.db");
        StateIntegrityTests.DamagePages(database);
        File.Delete(Path.Combine(backups, "boot-id"));

        var (exit, _, stderr) = await rig.RunToExitAsync(["daemon", "--state-dir", rig.StateDir]);

        Assert.Equal(70, exit);
        Assert.Contains("storage_corrupt", stderr, StringComparison.Ordinal);
        Assert.Contains(good[^1], stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(" ready pid=", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("OperationCanceledException", stderr, StringComparison.Ordinal);
        Assert.Equal(good, Directory.GetFiles(backups, "jobs-*.db").Order(StringComparer.Ordinal).ToArray());

        // The documented restore: replace the database (and any -wal/-shm) with the newest backup.
        File.Delete(database + "-wal");
        File.Delete(database + "-shm");
        File.Copy(good[^1], database, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(database, StateDirectory.PrivateFile);
        }
        await rig.StartDaemonAsync();
        Assert.Equal(JobStatus.Completed, (await rig.GetAsync(accepted.Job.JobId)).Job!.Status);
    }

    [Fact]
    public async Task Stop_works_on_a_loosened_state_directory_and_names_the_fix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var daemon = await rig.StartDaemonAsync();
        File.SetUnixFileMode(rig.StateDir, StateDirectory.PrivateDir | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        try
        {
            var (refusedExit, _, refusedError) = await rig.RunToExitAsync(["client", "list", "--state-dir", rig.StateDir]);
            Assert.Equal(78, refusedExit);
            Assert.Contains($"chmod 700 \"{rig.StateDir}\"", refusedError, StringComparison.Ordinal);

            var (exit, stdout, stderr) = await rig.RunToExitAsync(["stop", "--state-dir", rig.StateDir]);

            Assert.Equal(0, exit);
            Assert.Contains("Stopped daemon", stdout, StringComparison.Ordinal);
            Assert.Contains($"chmod 700 \"{rig.StateDir}\"", stderr, StringComparison.Ordinal);
            Assert.True(daemon.WaitForExit(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            File.SetUnixFileMode(rig.StateDir, StateDirectory.PrivateDir);
        }
    }
}
