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
        Assert.Equal(JobErrors.InvalidRequest, stop.Execute(queued.JobId).Error);
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
