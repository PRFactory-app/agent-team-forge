using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class StopAgentTests
{
    [Fact]
    public void Fenced_job_with_no_live_marked_process_can_be_stopped()
    {
        using var f = new JobFixture();
        var job = f.Submit("fenced");
        var claim = f.Store.BeginNextAttempt()!;
        var run = new RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation);
        f.Store.RecordSession(run, "native-1");
        Assert.True(f.Store.EndUnsuccessfully(run, JobStatus.NeedsReconciliation, "interactive_completion_unobserved"));
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => new OwnedBackend());

        var stopped = new StopAgent(f.Store, JobFixture.Operator, catalog).Execute(job.JobId);

        Assert.Equal("agent_stopped", stopped.Outcome);
        Assert.Equal(JobStatus.Cancelled, f.Store.GetJob(job.JobId)!.Status);
        Assert.False(f.Store.IsSessionFenced(job.JobId));
    }
    [Fact]
    public void Stopping_idle_parent_cancels_its_queued_deferred_child()
    {
        using var f = new JobFixture();
        var parent = f.Submit("parent");
        var claim = f.Store.BeginNextAttempt()!;
        var run = new RunRef(parent.JobId, claim.RunId, claim.Generation, claim.Correlation);
        f.Store.RecordSession(run, "native-1");
        var child = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept())
            .Execute(new FollowUpRequest(parent.JobId, "next", "child") { Defer = true }).Job!;
        f.Store.Complete(run, "done");
        var backend = new OwnedBackend();
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);

        Assert.Equal("agent_stopped", new StopAgent(f.Store, JobFixture.Operator, catalog).Execute(parent.JobId).Outcome);
        var stored = f.NewStore().GetJob(child.JobId)!;
        Assert.Equal((JobStatus.Cancelled, "parent_stopped", 0), (stored.Status, stored.ReasonCode, stored.Attempts));
        Assert.Null(f.Store.BeginNextAttempt());
    }

    [Fact]
    public void Stopping_agent_by_earlier_job_cancels_deferred_turn_of_a_later_session_job()
    {
        using var f = new JobFixture();
        var parent = f.Submit("parent");
        var claim = f.Store.BeginNextAttempt()!;
        var run = new RunRef(parent.JobId, claim.RunId, claim.Generation, claim.Correlation);
        f.Store.RecordSession(run, "native-1");
        f.Store.Complete(run, "done");
        var follow = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept());
        var turn = follow.Execute(new FollowUpRequest(parent.JobId, "second", "turn-2")).Job!;
        var turnClaim = f.Store.BeginNextAttempt()!;
        Assert.Equal(turn.JobId, turnClaim.Job.JobId);
        var turnRun = new RunRef(turn.JobId, turnClaim.RunId, turnClaim.Generation, turnClaim.Correlation);
        f.Store.RecordSession(turnRun, "native-1");
        var deferred = follow.Execute(new FollowUpRequest(turn.JobId, "third", "turn-3") { Defer = true }).Job!;
        f.Store.Complete(turnRun, "done");
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => new OwnedBackend());

        Assert.Equal("agent_stopped", new StopAgent(f.Store, JobFixture.Operator, catalog).Execute(parent.JobId).Outcome);
        Assert.Equal((JobStatus.Cancelled, "parent_stopped"),
            (f.Store.GetJob(deferred.JobId)!.Status, f.Store.GetJob(deferred.JobId)!.ReasonCode));
        Assert.Null(f.Store.BeginNextAttempt());
    }

    [Fact]
    public void Parent_timeout_keeps_queued_deferred_child()
    {
        using var f = new JobFixture();
        var parent = f.Submit("parent");
        var claim = f.Store.BeginNextAttempt()!;
        f.Store.RecordSession(new RunRef(parent.JobId, claim.RunId, claim.Generation, claim.Correlation), "native-1");
        var child = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept())
            .Execute(new FollowUpRequest(parent.JobId, "next", "child") { Defer = true }).Job!;

        Assert.True(f.Store.CancelOwned(parent.JobId, "timeout").Changed);
        Assert.Equal(JobStatus.Queued, f.Store.GetJob(child.JobId)!.Status);
    }

    [Fact]
    public void Completed_job_can_stop_only_its_owned_idle_session()
    {
        using var f = new JobFixture();
        var job = f.Submit("owned-idle");
        var claim = f.Store.BeginNextAttempt()!;
        var run = new RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation);
        Assert.True(f.Store.RecordSession(run, "native-1"));
        Assert.True(f.Store.Complete(run, "done"));
        var backend = new OwnedBackend();
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var stop = new StopAgent(f.Store, JobFixture.Operator, catalog);

        Assert.Equal("unchanged", new StopJob(f.Store, JobFixture.Operator, _ => { }).Execute(job.JobId).Outcome);
        Assert.Equal("agent_stopped", stop.Execute(job.JobId).Outcome);
        Assert.Equal("native-1", backend.StoppedSession);
        Assert.Equal("agent_not_running", stop.Execute(job.JobId).Outcome);
        Assert.Equal(JobErrors.NotFound, new StopAgent(f.Store,
            new BoundPrincipal("someone-else", JobFixture.Operator.Team, JobFixture.Operator.Agent), catalog).Execute(job.JobId).Error);
        Assert.Equal(JobStatus.Completed, f.Store.GetJob(job.JobId)!.Status);

        var queued = f.Submit("still-queued");
        var refused = stop.Execute(queued.JobId);
        Assert.Equal(JobErrors.InvalidRequest, refused.Error);
        Assert.False(string.IsNullOrEmpty(refused.Detail));
    }

    static (JobFixture Fixture, RunRef Run, string JobId) RunningJob(string key)
    {
        var f = new JobFixture();
        var job = f.Submit(key);
        var claim = f.Store.BeginNextAttempt()!;
        var run = new RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation);
        f.Store.RecordSession(run, "native-1");
        return (f, run, job.JobId);
    }

    static JobResult StopJobMustNotRun(string _) => throw new InvalidOperationException("stop_job must not be called");

    [Fact]
    public void Running_idle_agent_whose_turn_finished_keeps_its_result_and_closes()
    {
        var (f, run, jobId) = RunningJob("finished");
        using var _ = f;
        var backend = new OwnedBackend();
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var stop = new StopAgent(f.Store, JobFixture.Operator, catalog, _ => true,
            _ => { f.Store.Complete(run, "done"); return true; }, StopJobMustNotRun, TimeSpan.Zero);

        var stopped = stop.Execute(jobId);

        Assert.Equal("agent_stopped", stopped.Outcome);
        Assert.Equal(JobStatus.Completed, f.Store.GetJob(jobId)!.Status);
        Assert.Equal("native-1", backend.StoppedSession);
    }

    [Fact]
    public void Running_idle_agent_that_completes_during_the_settle_wait_is_not_cancelled()
    {
        var (f, run, jobId) = RunningJob("settling");
        using var _ = f;
        var calls = 0;
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => new OwnedBackend());
        var stop = new StopAgent(f.Store, JobFixture.Operator, catalog, _ => true,
            _ => ++calls > 2 && f.Store.Complete(run, "done"), StopJobMustNotRun, TimeSpan.FromSeconds(3));

        Assert.Equal("agent_stopped", stop.Execute(jobId).Outcome);
        Assert.Equal(JobStatus.Completed, f.Store.GetJob(jobId)!.Status);
    }

    [Fact]
    public void Running_idle_agent_without_completion_is_cancelled_and_closed()
    {
        var (f, _, jobId) = RunningJob("background-task");
        using var _ = f;
        var backend = new OwnedBackend();
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var stop = new StopAgent(f.Store, JobFixture.Operator, catalog, _ => true, _ => false,
            new StopJob(f.Store, JobFixture.Operator, _ => { }).Execute, TimeSpan.Zero);

        Assert.Equal("agent_stopped", stop.Execute(jobId).Outcome);
        Assert.Equal(JobStatus.Cancelled, f.Store.GetJob(jobId)!.Status);
        Assert.Equal("native-1", backend.StoppedSession);
    }

    [Fact]
    public void Running_working_agent_is_refused_with_detail_and_left_running()
    {
        var (f, _, jobId) = RunningJob("working");
        using var _ = f;
        var child = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept())
            .Execute(new FollowUpRequest(jobId, "next", "child") { Defer = true }).Job!;
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => new OwnedBackend());
        var stop = new StopAgent(f.Store, JobFixture.Operator, catalog, _ => false, _ => false,
            StopJobMustNotRun, TimeSpan.Zero);

        var refused = stop.Execute(jobId);

        Assert.Equal(JobErrors.InvalidRequest, refused.Error);
        Assert.False(string.IsNullOrEmpty(refused.Detail));
        Assert.Equal(JobStatus.Running, f.Store.GetJob(jobId)!.Status);
        Assert.Equal(JobStatus.Queued, f.Store.GetJob(child.JobId)!.Status);
    }

    sealed class OwnedBackend : IJobBackend, IInteractiveSessionStop
    {
        public string? StoppedSession { get; private set; }
        public IBackendRun Start(BackendRequest request) => throw new InvalidOperationException("not dispatched");
        public bool HasIdleSession(string sessionId) => StoppedSession is null && sessionId == "native-1";
        public bool StopIdleSession(string sessionId)
        {
            if (StoppedSession is not null || sessionId != "native-1")
            {
                return false;
            }
            StoppedSession = sessionId;
            return true;
        }
        public void StopAllIdleSessions() { }
    }
}
