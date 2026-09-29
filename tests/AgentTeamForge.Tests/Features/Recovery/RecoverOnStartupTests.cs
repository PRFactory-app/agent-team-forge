using AgentTeamForge.Business.Features.Recovery;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Recovery;

public sealed class RecoverOnStartupTests
{
    [Fact]
    public void Uncertain_attempts_are_quarantined_while_unattempted_and_completed_work_is_kept()
    {
        using var f = new JobFixture();
        var done = f.Submit("done");
        var doneClaim = f.Store.BeginNextAttempt()!;
        Assert.True(f.Store.Complete(new RunRef(done.JobId, doneClaim.RunId, doneClaim.Generation, doneClaim.Correlation), "r"));
        var started = f.Submit("started");
        f.Store.BeginNextAttempt();
        var queued = f.Submit("queued");

        var quarantined = new RecoverOnStartup(f.Store).Execute();

        Assert.Equal([started.JobId], quarantined);
        Assert.Equal(JobStatus.NeedsReconciliation, f.Store.GetJob(started.JobId)!.Status);
        Assert.Equal(JobStatus.Completed, f.Store.GetJob(done.JobId)!.Status);
        Assert.Equal(JobStatus.Queued, f.Store.GetJob(queued.JobId)!.Status);
        Assert.Equal(queued.JobId, f.Store.BeginNextAttempt()!.Job.JobId);
        Assert.Null(f.Store.BeginNextAttempt());
    }

    [Theory]
    [InlineData("interactive_completion_unobserved", true)]
    [InlineData("interactive_delivery_not_confirmed", false)]
    public void Only_an_acknowledged_idle_turn_settles_as_interrupted(string reason, bool settles)
    {
        using var f = new JobFixture();
        var job = f.Submit("turn");
        var claim = f.Store.BeginNextAttempt()!;
        f.Store.EndUnsuccessfully(new RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation), JobStatus.NeedsReconciliation, reason);
        Assert.Equal(settles, f.Store.SettleInterrupted(job.JobId));
        Assert.Equal(settles ? JobStatus.Failed : JobStatus.NeedsReconciliation, f.Store.GetJob(job.JobId)!.Status);
    }

    [Fact]
    public void Verified_reattachment_restores_same_run_and_unattached_failure_releases_fence()
    {
        using var f = new JobFixture();
        var live = f.Submit("live");
        var claim = f.Store.BeginNextAttempt()!;
        var reference = new RunRef(live.JobId, claim.RunId, claim.Generation, claim.Correlation);
        Assert.True(f.Store.RecordSession(reference, "native"));
        f.Store.RecordStartup(reference, "submitted");
        Assert.Equal([live.JobId], new RecoverOnStartup(f.Store).Execute());
        Assert.Empty(new RecoverOnStartup(f.Store).Execute());
        Assert.Equal([live.JobId], f.Store.RestartCandidates());
        Assert.True(f.Store.ReattachQuarantined(reference));
        Assert.Equal(JobStatus.Running, f.Store.GetJob(live.JobId)!.Status);
        Assert.False(f.Store.IsSessionFenced(live.JobId));
        Assert.Equal("started", Assert.Single(f.Store.GetRuns(live.JobId)).State);
        Assert.True(f.Store.Complete(reference, "done"));

        var lost = f.Submit("lost");
        f.Store.BeginNextAttempt();
        Assert.Equal([lost.JobId], new RecoverOnStartup(f.Store).Execute());
        Assert.True(f.Store.FailUnattached(lost.JobId));
        Assert.Equal((JobStatus.Failed, "daemon_restart_agent_gone"),
            (f.Store.GetJob(lost.JobId)!.Status, f.Store.GetJob(lost.JobId)!.ReasonCode));
        Assert.False(f.Store.IsSessionFenced(lost.JobId));
    }
}
