using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class FollowUpJobTests
{
    static ScriptedBackend Agent(string sessionId) => new(r =>
    [
        new BackendEvidence.Ack(r.Correlation),
        new BackendEvidence.Session(r.Correlation, r.ResumeSessionId ?? sessionId),
        new BackendEvidence.Result(r.Correlation, "turn done"),
    ]);

    static async Task DispatchNext(JobFixture f, BackendCatalog catalog)
    {
        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        await dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
    }

    static AcceptJob Accept(JobFixture f, BackendCatalog catalog) =>
        new(f.Store, JobFixture.Operator, f.Limits, f.TestProfile, f.Admission, catalog.Names);

    [Fact]
    public async Task Follow_up_resumes_the_parent_session_on_the_parent_backend_and_cwd()
    {
        using var f = new JobFixture();
        using var cwd = new TempStateDir();
        var claude = Agent("sess-1");
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => Agent("wrong")).Register(BackendCatalog.Claude, () => claude);
        var accept = Accept(f, catalog);

        var parent = accept.Execute(new SubmitJobRequest("p", "first", null, false) { Backend = BackendCatalog.Claude, Cwd = cwd.Path }).Job!;
        await DispatchNext(f, catalog);
        Assert.Equal("sess-1", f.Get().Execute(parent.JobId).Job!.SessionId);

        var followUp = new FollowUpJob(f.Store, JobFixture.Operator, accept);
        var child = followUp.Execute(new FollowUpRequest(parent.JobId, "second", "c"));
        Assert.Equal("accepted", child.Outcome);
        Assert.Equal("existing", followUp.Execute(new FollowUpRequest(parent.JobId, "second", "c")).Outcome);
        await DispatchNext(f, catalog);

        var resumed = claude.Started[1];
        Assert.Equal("sess-1", resumed.ResumeSessionId);
        Assert.Equal(cwd.Path, resumed.WorkingDirectory);
        var view = f.Get().Execute(child.Job!.JobId).Job!;
        Assert.Equal((JobStatus.Completed, BackendCatalog.Claude, parent.JobId, "sess-1"), (view.Status, view.Backend, view.ParentJobId, view.SessionId));
    }

    [Fact]
    public void Follow_up_is_refused_until_the_parent_has_finished_with_a_session()
    {
        using var f = new JobFixture();
        var accept = f.Accept();
        var parent = f.Submit("p");
        var followUp = new FollowUpJob(f.Store, JobFixture.Operator, accept);

        Assert.Equal(JobErrors.ParentNotReady, followUp.Execute(new FollowUpRequest(parent.JobId, "next", "c")).Error);
        Assert.Equal(JobErrors.NotFound, followUp.Execute(new FollowUpRequest("job_missing", "next", "c")).Error);
        Assert.Equal(1, f.Store.CountUnattemptedIntents());
    }

    [Fact]
    public async Task Follow_up_is_refused_when_a_parent_with_a_session_needs_reconciliation()
    {
        using var f = new JobFixture();
        var backend = new ScriptedBackend(r =>
        [
            new BackendEvidence.Session(r.Correlation, "sess-uncertain"),
            new BackendEvidence.ProtocolError("uncertain"),
        ]);
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var accept = Accept(f, catalog);
        var parent = accept.Execute(new SubmitJobRequest("p", "first", null, false)).Job!;
        await DispatchNext(f, catalog);

        Assert.Equal(JobStatus.NeedsReconciliation, f.Store.GetJob(parent.JobId)!.Status);
        Assert.Equal(JobErrors.ParentNotReady,
            new FollowUpJob(f.Store, JobFixture.Operator, accept).Execute(new FollowUpRequest(parent.JobId, "next", "c")).Error);
        Assert.Equal(0, f.Store.CountUnattemptedIntents());
    }

    [Fact]
    public async Task Dispatch_runs_the_backend_named_by_the_job()
    {
        using var f = new JobFixture();
        var fake = Agent("f");
        var claude = Agent("c");
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => fake).Register(BackendCatalog.Claude, () => claude);

        Accept(f, catalog).Execute(new SubmitJobRequest("k", "x", null, false) { Backend = BackendCatalog.Claude });
        await DispatchNext(f, catalog);

        Assert.Single(claude.Started);
        Assert.Empty(fake.Started);
        Assert.Equal(JobErrors.BackendUnavailable,
            Accept(f, catalog).Execute(new SubmitJobRequest("k2", "x", null, false) { Backend = BackendCatalog.Codex }).Error);
    }

    [Fact]
    public async Task A_job_whose_backend_is_no_longer_configured_fails_without_starting()
    {
        using var f = new JobFixture();
        var catalog = new BackendCatalog().Register(BackendCatalog.Pi, () => Agent("p"));
        var job = Accept(f, catalog).Execute(new SubmitJobRequest("k", "x", null, false) { Backend = BackendCatalog.Pi }).Job!;

        await DispatchNext(f, new BackendCatalog());

        var stored = f.Store.GetJob(job.JobId)!;
        Assert.Equal((JobStatus.Failed, "backend_unavailable"), (stored.Status, stored.ReasonCode));
    }
}
