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
    public async Task Explicit_release_abandons_a_stuck_claude_post_for_the_agent()
    {
        using var fixture = new JobFixture();
        var catalog = Catalog(new ScriptedBackend(request =>
        [
            new BackendEvidence.Session(request.Correlation, "claude-stuck"),
            new BackendEvidence.Result(request.Correlation, "ready")
        ]));
        var parent = Accept(fixture, catalog).Execute(new SubmitJobRequest("parent", "work", null, false)
        { Backend = BackendCatalog.Claude, TargetAgent = "stuckagent" }).Job!;
        using var dispatcher = new DispatchJob(fixture.Store, catalog, fixture.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        var follow = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog));
        var child = follow.Execute(new FollowUpRequest(parent.JobId, "next", "next")).Job!;
        var claim = fixture.Store.BeginNativeClaudeAttempt(parent.JobId, "/tmp/claude-home", _ => true)!;
        fixture.Store.RecordNativeClaudePost(child.JobId, claim.Correlation); // bridge wrote, no receipt yet
        Assert.Equal(JobErrors.ParentNotReady, follow.Execute(new FollowUpRequest(parent.JobId, "retry", "retry")).Error);

        var stop = new StopJob(fixture.Store, JobFixture.Operator, dispatcher.CancelRunning, releaseNative: dispatcher.ReleaseNative);
        fixture.Store.EndUnsuccessfully(new(child.JobId, claim.RunId, claim.Generation, claim.Correlation), JobStatus.NeedsReconciliation, "native_delivery_unresolved");
        Assert.Equal("native_released", stop.Release(child.JobId).Outcome);
        Assert.Equal(JobStatus.Cancelled, fixture.Store.GetJob(child.JobId)!.Status);
        Assert.Empty(fixture.Store.UnresolvedNativeClaudeAttempts());
        Assert.False(fixture.Store.SettleNativeClaudeAttempt(child.JobId, claim.Correlation, "late"));
        Assert.NotNull(follow.Execute(new FollowUpRequest(parent.JobId, "retry", "retry")).Job);
        Assert.NotNull(Accept(fixture, catalog).Execute(new SubmitJobRequest("fresh", "work", null, false)
        { Backend = BackendCatalog.Claude, TargetAgent = "stuckagent" }).Job);
    }

    [Fact]
    public async Task Native_turn_idle_on_a_background_task_past_its_limit_fails_and_releases_the_next_follow_up()
    {
        using var fixture = new JobFixture();
        using var state = new TempStateDir();
        var claudeHome = Path.Combine(state.Path, "claude");
        var catalog = Catalog(new ScriptedBackend(request =>
        [
            new BackendEvidence.Session(request.Correlation, "claude-bg"),
            new BackendEvidence.Result(request.Correlation, "ready")
        ]));
        var parent = Accept(fixture, catalog).Execute(new SubmitJobRequest("parent", "work", null, false)
        { Backend = BackendCatalog.Claude, TargetAgent = "bgagent" }).Job!;
        using var dispatcher = new DispatchJob(fixture.Store, catalog, fixture.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { })
        { NativeBackgroundWaitLimit = TimeSpan.FromMilliseconds(200) };
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        var follow = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog));
        var child = follow.Execute(new FollowUpRequest(parent.JobId, "next", "next")).Job!;
        var claim = fixture.Store.BeginNativeClaudeAttempt(parent.JobId, claudeHome, _ => true)!;
        fixture.Store.RecordNativeClaudePost(child.JobId, claim.Correlation);
        // The turn ends on a background build whose task notification never arrives.
        var dir = Directory.CreateDirectory(Path.Combine(claudeHome, "projects", "scratch"));
        File.WriteAllLines(Path.Combine(dir.FullName, "claude-bg.jsonl"),
        [
            $$$"""{"type":"user","isSidechain":false,"sessionId":"claude-bg","message":{"role":"user","content":"next atf-corr:{{{claim.Correlation}}}"}}""",
            """{"type":"assistant","isSidechain":false,"sessionId":"claude-bg","message":{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"tool-1","name":"Bash","input":{"run_in_background":true}}]}}""",
            """{"type":"user","isSidechain":false,"sessionId":"claude-bg","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tool-1","content":"Running in background"}]},"toolUseResult":{"backgroundTaskId":"task-1"}}""",
            """{"type":"assistant","isSidechain":false,"sessionId":"claude-bg","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"waiting"}]}}""",
        ]);

        dispatcher.ReconcileNativeClaude();
        Assert.Equal(JobStatus.Running, fixture.Store.GetJob(child.JobId)!.Status); // The wait starts.
        await Task.Delay(300, TestContext.Current.CancellationToken);
        dispatcher.ReconcileNativeClaude();

        var record = fixture.Store.GetJob(child.JobId)!;
        Assert.Equal(JobStatus.Failed, record.Status);
        Assert.Equal("background_wait_expired", record.ReasonCode);
        var next = follow.Execute(new FollowUpRequest(parent.JobId, "after", "after")).Job!;
        Assert.Equal(next.JobId, fixture.Store.BeginNativeClaudeAttempt(parent.JobId, claudeHome, _ => true)!.Job.JobId);
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
