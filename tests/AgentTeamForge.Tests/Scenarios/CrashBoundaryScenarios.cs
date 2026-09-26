using System.Diagnostics;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.Tests.Scenarios;

/// <summary>
/// Abrupt daemon death (SIGKILL of itself at a named durable boundary, test
/// profile only), then restart against the same database.
/// </summary>
[Trait("Category", "Scenario")]
public sealed class CrashBoundaryScenarios
{
    [Fact]
    public async Task Crash_before_acceptance_commit_leaves_no_job()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var daemon = await rig.StartDaemonAsync("--test-crash-at", DurabilityCheckpoints.AcceptBeforeCommit);

        var lost = await rig.SubmitAsync("k-before", "x");
        Assert.False(lost.Ok);
        await Bounded.Until(() => daemon.HasExited, "daemon crash");

        await rig.StartDaemonAsync();
        var retry = await rig.SubmitAsync("k-before", "x");
        Assert.Equal("accepted", retry.Outcome);
        await rig.WaitForStatusAsync(retry.Job!.JobId, JobStatus.Completed);
        Assert.Equal(1, rig.Invocations(retry.Job.JobId));
    }

    [Fact]
    public async Task Crash_after_acceptance_commit_before_response_is_recovered_by_same_key()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var daemon = await rig.StartDaemonAsync("--test-crash-at", DurabilityCheckpoints.AcceptAfterCommit);

        var lost = await rig.SubmitAsync("k-after", "x");
        Assert.False(lost.Ok);
        await Bounded.Until(() => daemon.HasExited, "daemon crash");

        await rig.StartDaemonAsync();
        var retry = await rig.SubmitAsync("k-after", "x");
        Assert.Equal("existing", retry.Outcome);
        var done = await rig.WaitForStatusAsync(retry.Job!.JobId, JobStatus.Completed);
        Assert.Equal(1, done.Job!.Attempts);
        Assert.Equal(1, rig.Invocations(retry.Job.JobId));
    }

    [Fact]
    public async Task Crash_after_attempt_start_is_quarantined_not_replayed()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var daemon = await rig.StartDaemonAsync("--test-crash-at", DurabilityCheckpoints.AttemptAfterCommit);

        await rig.SubmitAsync("k-attempt", "x");
        await Bounded.Until(() => daemon.HasExited, "daemon crash");

        await rig.StartDaemonAsync();
        var retry = await rig.SubmitAsync("k-attempt", "x");
        Assert.Equal("existing", retry.Outcome);
        Assert.Equal(JobStatus.NeedsReconciliation, retry.Job!.Status);
        Assert.Equal("daemon_restart_uncertain", retry.Job.ReasonCode);
        Assert.Equal(1, retry.Job.Attempts);
        Assert.Equal(0, rig.Invocations(retry.Job.JobId));
    }

    [Fact]
    public async Task Daemon_killed_during_an_acked_run_quarantines_it_on_restart()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var daemon = await rig.StartDaemonAsync();
        var accepted = await rig.SubmitAsync("k-running", "x", hold: true);
        await rig.WaitForAckAsync(accepted.Job!.JobId);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(rig.StateDir, "jobs.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT backend_pid FROM runs WHERE job_id=$id";
        command.Parameters.AddWithValue("$id", accepted.Job.JobId);
        var childPid = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

        OwnedProcesses.KillAbruptly(daemon);
        await rig.StartDaemonAsync();

        var after = await rig.GetAsync(accepted.Job.JobId);
        Assert.Equal(JobStatus.NeedsReconciliation, after.Job!.Status);
        Assert.Equal(1, after.Job.Attempts);
        Assert.Equal(1, rig.Invocations(accepted.Job.JobId));
        await Bounded.Until(() =>
        {
            try
            {
                using var child = Process.GetProcessById(childPid);
                return child.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }, "orphaned backend child to stop after restart");
    }

    [Theory]
    [InlineData(FakeBehavior.ExitAfterReceipt, "backend_eof")]
    [InlineData(FakeBehavior.EofAfterAck, "backend_eof")]
    [InlineData(FakeBehavior.MismatchedCorrelation, "backend_eof")]
    [InlineData(FakeBehavior.Hang, "backend_timeout")]
    public async Task Real_child_without_correlated_result_never_completes(string behavior, string reason)
    {
        using var rig = new SpikeRig();
        await rig.InitAsync(maxRuntimeSeconds: 2);
        await rig.StartDaemonAsync();

        var accepted = await rig.SubmitAsync("k-" + behavior, "x", behavior);
        var end = await rig.WaitForStatusAsync(accepted.Job!.JobId, JobStatus.NeedsReconciliation, JobStatus.Completed, JobStatus.Failed);

        Assert.Equal(JobStatus.NeedsReconciliation, end.Job!.Status);
        Assert.Equal(reason, end.Job.ReasonCode);
        Assert.Null(end.Job.Result);
        Assert.Equal(1, rig.Invocations(accepted.Job.JobId));
    }

    [Fact]
    public async Task Injected_completion_write_failure_reports_uncertainty_not_success()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var daemon = await rig.StartDaemonAsync("--test-fail-at", DurabilityCheckpoints.CompleteBeforeCommit);

        var accepted = await rig.SubmitAsync("k-write", "x");

        // Dispatch halts and the daemon stops admission instead of queueing work it will not run.
        await Bounded.Until(() => daemon.HasExited, "daemon to stop after a failed completion write");
        Assert.NotEqual(0, daemon.ExitCode);

        await rig.StartDaemonAsync();
        var end = (await rig.GetAsync(accepted.Job!.JobId)).Job!;
        Assert.Equal(JobStatus.NeedsReconciliation, end.Status);
        Assert.Equal("completion_write_failed", end.ReasonCode);
        Assert.Null(end.Result);
        var next = await rig.SubmitAsync("k-next", "y");
        await rig.WaitForStatusAsync(next.Job!.JobId, JobStatus.Completed);
    }
}
