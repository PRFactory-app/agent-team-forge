using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Scenarios;

[Trait("Category", "Scenario")]
public sealed class StartupBackupScenarios
{
    [Fact]
    public async Task Failed_backup_is_logged_and_daemon_still_serves_jobs()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        File.WriteAllText(Path.Combine(rig.StateDir, "backups"), "blocked");

        await rig.StartDaemonAsync();

        Assert.Contains(rig.DaemonLog, line => line.Contains("backup failed:", StringComparison.Ordinal));
        var accepted = await Bounded.Until(async () =>
        {
            var attempt = await rig.SubmitAsync("backup-failed", "still works");
            return attempt.Error == IpcProtocol.DaemonUnavailable ? null : attempt;
        }, "daemon accepts submit after failed backup");
        Assert.True(accepted.Ok, $"{accepted.Error}; daemon log: {string.Join(" | ", rig.DaemonLog)}");
        await rig.WaitForStatusAsync(accepted.Job!.JobId, JobStatus.Completed);
    }
}
