using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class NativeClaudeDeliveryTests
{
    static BackendCatalog Catalog(IJobBackend backend) => new BackendCatalog().Register(BackendCatalog.Claude, () => backend);

    static AcceptJob Accept(JobFixture fixture, BackendCatalog catalog) =>
        new(fixture.Store, JobFixture.Operator, fixture.Limits, fixture.TestProfile, fixture.Admission, catalog.Names);

    [Fact]
    public async Task Idle_child_claims_once_and_a_receipt_releases_the_fence()
    {
        using var fixture = new JobFixture();
        var catalog = Catalog(new ScriptedBackend(request =>
        [
            new BackendEvidence.Session(request.Correlation, "claude-session"),
            new BackendEvidence.Result(request.Correlation, "ready")
        ]));
        var parent = Accept(fixture, catalog).Execute(new SubmitJobRequest("parent", "work", null, false)
        { Backend = BackendCatalog.Claude, TargetAgent = "sameagent" }).Job!;
        using var dispatcher = new DispatchJob(fixture.Store, catalog, fixture.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);

        var follow = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog));
        var child = follow.Execute(new FollowUpRequest(parent.JobId, "next", "next")).Job!;
        Assert.Contains(";native_claude=1", fixture.Store.GetJob(child.JobId)!.Options);
        var claim = fixture.Store.BeginNativeClaudeAttempt(parent.JobId, "/tmp/claude-home", _ => true)!;
        Assert.Equal(child.JobId, claim.Job.JobId);
        Assert.Null(fixture.Store.BeginNativeClaudeAttempt(parent.JobId, "/tmp/claude-home", _ => true));
        Assert.Null(fixture.Store.BeginNextAttempt());
        Assert.Equal(JobErrors.ParentNotReady, follow.Execute(new FollowUpRequest(parent.JobId, "duplicate", "other")).Error);
        Assert.Equal("existing", follow.Execute(new FollowUpRequest(parent.JobId, "next", "next")).Outcome);
        fixture.Store.RecordNativeClaudePost(child.JobId, claim.Correlation);
        fixture.Store.RecordNativeClaudeReceipt(child.JobId, claim.Correlation);
        Assert.Equal("received", fixture.Store.NativeClaudeAttempt(child.JobId)!.State);
        Assert.True(fixture.Store.SettleNativeClaudeAttempt(child.JobId, claim.Correlation, "answer"));
        Assert.False(fixture.Store.SettleNativeClaudeAttempt(child.JobId, claim.Correlation, "duplicate"));
        Assert.Equal("answer", fixture.Store.GetJob(child.JobId)!.ResultText);
    }

    [Fact]
    public void Busy_child_waits_until_parent_turn_ends()
    {
        using var fixture = new JobFixture();
        var catalog = Catalog(new ScriptedBackend(request => [new BackendEvidence.Session(request.Correlation, "claude-busy")]));
        var parent = Accept(fixture, catalog).Execute(new SubmitJobRequest("parent", "work", null, false)
        { Backend = BackendCatalog.Claude }).Job!;
        var parentClaim = fixture.Store.BeginNextAttempt()!;
        var parentRef = new RunRef(parent.JobId, parentClaim.RunId, parentClaim.Generation, parentClaim.Correlation);
        Assert.True(fixture.Store.RecordSession(parentRef, "claude-busy"));
        var child = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog))
            .Execute(new FollowUpRequest(parent.JobId, "next", "next") { Defer = true }).Job!;
        Assert.Null(fixture.Store.BeginNativeClaudeAttempt(parent.JobId, "/tmp/claude-home", _ => true));
        Assert.True(fixture.Store.Complete(parentRef, "finished"));
        Assert.Equal(child.JobId, fixture.Store.BeginNativeClaudeAttempt(parent.JobId, "/tmp/claude-home", _ => true)!.Job.JobId);
    }

    [Fact]
    public async Task Dead_child_and_prewrite_failure_resume_but_unresolved_post_survives_restart()
    {
        using var fixture = new JobFixture();
        var catalog = Catalog(new ScriptedBackend(request =>
        [
            new BackendEvidence.Session(request.Correlation, "claude-recovery"),
            new BackendEvidence.Result(request.Correlation, "ready")
        ]));
        var parent = Accept(fixture, catalog).Execute(new SubmitJobRequest("parent", "work", null, false)
        { Backend = BackendCatalog.Claude }).Job!;
        using var dispatcher = new DispatchJob(fixture.Store, catalog, fixture.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        var follow = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog));
        var dead = follow.Execute(new FollowUpRequest(parent.JobId, "dead", "dead")).Job!;
        Assert.Null(fixture.Store.BeginNativeClaudeAttempt(parent.JobId, "/tmp/claude-home", _ => false));
        Assert.Equal(dead.JobId, fixture.Store.BeginNextAttempt()!.Job.JobId); // ordinary resume carrier
        fixture.Store.Complete(new RunRef(dead.JobId, fixture.Store.GetRuns(dead.JobId)[0].RunId, 1,
            fixture.Store.GetRuns(dead.JobId)[0].Correlation), "resumed");

        var retry = follow.Execute(new FollowUpRequest(parent.JobId, "retry", "retry")).Job!;
        var first = fixture.Store.BeginNativeClaudeAttempt(parent.JobId, "/tmp/claude-home", _ => true)!;
        Assert.True(fixture.Store.RevertNativeClaudeAttempt(new RunRef(retry.JobId, first.RunId, 1, first.Correlation)));
        var resumed = fixture.Store.BeginNextAttempt()!;
        Assert.Equal(retry.JobId, resumed.Job.JobId);
        Assert.True(fixture.Store.Complete(new RunRef(retry.JobId, resumed.RunId, resumed.Generation, resumed.Correlation), "resumed"));
        var restarted = fixture.NewStore();
        var blocked = follow.Execute(new FollowUpRequest(parent.JobId, "another", "another"));
        // The reverted carrier can run normally; a separate claimed post is tested below.
        Assert.Equal("accepted", blocked.Outcome);
        var unresolved = restarted.BeginNativeClaudeAttempt(parent.JobId, "/tmp/claude-home", _ => true)!;
        restarted.QuarantineUncertainAttempts();
        Assert.Equal("posting", restarted.NativeClaudeAttempt(unresolved.Job.JobId)!.State);
        Assert.Equal(JobErrors.ParentNotReady, follow.Execute(new FollowUpRequest(parent.JobId, "fenced", "fenced")).Error);
        Assert.Null(restarted.BeginNextAttempt());
        restarted.RecordNativeClaudeReceipt(unresolved.Job.JobId, unresolved.Correlation);
        Assert.True(restarted.SettleNativeClaudeAttempt(unresolved.Job.JobId, unresolved.Correlation, "recovered"));
        Assert.Equal("accepted", follow.Execute(new FollowUpRequest(parent.JobId, "after receipt", "after")).Outcome);
    }

    [Fact]
    public void Oversize_claude_follow_up_uses_resume()
    {
        using var fixture = new JobFixture();
        var catalog = Catalog(new ScriptedBackend(_ => []));
        var parent = Accept(fixture, catalog).Execute(new SubmitJobRequest("parent", "work", null, false)
        { Backend = BackendCatalog.Claude }).Job!;
        var claim = fixture.Store.BeginNextAttempt()!;
        var run = new RunRef(parent.JobId, claim.RunId, claim.Generation, claim.Correlation);
        Assert.True(fixture.Store.RecordSession(run, "claude-large"));
        Assert.True(fixture.Store.Complete(run, "ready"));
        var child = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog))
            .Execute(new FollowUpRequest(parent.JobId, new string('x', 17_000), "large")).Job!;
        Assert.DoesNotContain("native_claude", fixture.Store.GetJob(child.JobId)!.Options);
        Assert.Null(fixture.Store.BeginNativeClaudeAttempt(parent.JobId, "/tmp/claude-home", _ => true));
        Assert.Equal(child.JobId, fixture.Store.BeginNextAttempt()!.Job.JobId);
    }
}
