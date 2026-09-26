using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Scenarios;

/// <summary>
/// Injected failure (test profile only) in a live daemon around the acceptance
/// commit: the reply must say whether acceptance is unknown or did not happen.
/// </summary>
[Trait("Category", "Scenario")]
public sealed class AcceptanceOutcomeScenarios
{
    [Fact]
    public async Task Failure_after_acceptance_commit_reports_outcome_unknown_and_same_key_runs_the_job_once()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var daemon = await rig.StartDaemonAsync("--test-fail-at", DurabilityCheckpoints.AcceptAfterCommit);

        var lost = await rig.SubmitAsync("k-unknown", "x");
        Assert.False(lost.Ok);
        Assert.Equal(IpcProtocol.OutcomeUnknown, lost.Error);

        var retry = await rig.SubmitAsync("k-unknown", "x");
        Assert.Equal("existing", retry.Outcome);
        var done = await rig.WaitForStatusAsync(retry.Job!.JobId, JobStatus.Completed);
        Assert.Equal(1, done.Job!.Attempts);
        Assert.Equal(1, rig.Invocations(retry.Job.JobId));
        Assert.False(daemon.HasExited);
    }

    [Fact]
    public async Task Failure_before_acceptance_commit_is_a_definite_error_not_an_unknown_outcome()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        await rig.StartDaemonAsync("--test-fail-at", DurabilityCheckpoints.AcceptBeforeCommit);

        var failed = await rig.SubmitAsync("k-before", "x");
        Assert.False(failed.Ok);
        Assert.Equal(IpcProtocol.InternalError, failed.Error);
    }
}
