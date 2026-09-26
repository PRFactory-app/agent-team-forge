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
}
