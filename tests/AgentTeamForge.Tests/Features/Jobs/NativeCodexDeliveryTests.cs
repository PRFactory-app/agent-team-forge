using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class NativeCodexDeliveryTests
{
    static BackendCatalog Catalog(IJobBackend backend) => new BackendCatalog().Register(BackendCatalog.Codex, () => backend);

    static AcceptJob Accept(JobFixture fixture, BackendCatalog catalog) =>
        new(fixture.Store, JobFixture.Operator, fixture.Limits, fixture.TestProfile, fixture.Admission, catalog.Names);

    [Fact]
    public async Task Busy_codex_follow_up_is_claimed_without_ending_the_parent_and_fenced_once()
    {
        using var fixture = new JobFixture();
        var backend = new ScriptedBackend(request => [new BackendEvidence.Session(request.Correlation, "thread-busy")]) { Hangs = true };
        var catalog = Catalog(backend);
        var parent = Accept(fixture, catalog).Execute(new SubmitJobRequest("parent", "work", null, false)
        { Backend = BackendCatalog.Codex, TargetAgent = "sameagent" }).Job!;
        using var dispatcher = new DispatchJob(fixture.Store, catalog, fixture.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        var parentRun = dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        await Bounded.Until(() => fixture.Store.GetJob(parent.JobId)?.SessionId == "thread-busy", "session binding");

        var follow = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog));
        var child = follow.Execute(new FollowUpRequest(parent.JobId, "next", "next") { Defer = true }).Job!;
        Assert.Contains(";native_codex=1", fixture.Store.GetJob(child.JobId)!.Options);
        Assert.Null(fixture.Store.BeginNativeCodexAttempt(_ => true, "/tmp/codex-home", _ => false));
        var native = fixture.Store.BeginNativeCodexAttempt(_ => true, "/tmp/codex-home")!;
        Assert.Equal(child.JobId, native.Job.JobId);
        Assert.Equal(JobStatus.Running, fixture.Store.GetJob(parent.JobId)!.Status);
        Assert.Null(fixture.Store.BeginNativeCodexAttempt(_ => true, "/tmp/codex-home"));
        Assert.Equal(JobErrors.ParentNotReady, follow.Execute(new FollowUpRequest(parent.JobId, "duplicate carrier", "other") { Defer = true }).Error);
        Assert.Equal("existing", follow.Execute(new FollowUpRequest(parent.JobId, "next", "next") { Defer = true }).Outcome);

        dispatcher.CancelRunning(parent.JobId);
        await parentRun.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Idle_codex_records_submission_and_restart_keeps_fence_until_receipt()
    {
        using var fixture = new JobFixture();
        var backend = new ScriptedBackend(request =>
        [
            new BackendEvidence.Session(request.Correlation, "thread-idle"),
            new BackendEvidence.Result(request.Correlation, "done")
        ]);
        var catalog = Catalog(backend);
        var parent = Accept(fixture, catalog).Execute(new SubmitJobRequest("parent", "work", null, false)
        { Backend = BackendCatalog.Codex, TargetAgent = "sameagent" }).Job!;
        using (var dispatcher = new DispatchJob(fixture.Store, catalog, fixture.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { }))
        {
            await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        }
        var follow = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog));
        var child = follow.Execute(new FollowUpRequest(parent.JobId, "next", "next")).Job!;
        var claim = fixture.Store.BeginNativeCodexAttempt(_ => true, "/tmp/codex-home")!;
        fixture.Store.RecordNativeSubmission(child.JobId, claim.Correlation, "submission-1");
        Assert.Equal("submission-1", fixture.Get().Execute(child.JobId).Job!.Delivery!.NativeSubmissionId);

        var restarted = fixture.NewStore();
        restarted.QuarantineUncertainAttempts();
        Assert.Equal("submission-1", restarted.NativeAttempt(child.JobId)!.SubmissionId);
        Assert.Equal(JobErrors.ParentNotReady,
            new FollowUpJob(restarted, JobFixture.Operator, fixture.Accept(restarted))
                .Execute(new FollowUpRequest(parent.JobId, "again", "another")).Error);
        Assert.Equal(JobErrors.ParentNotReady, Accept(fixture, catalog).Execute(new SubmitJobRequest("replacement", "work", null, false)
        { Backend = BackendCatalog.Codex, TargetAgent = "sameagent" }).Error);
        Assert.True(restarted.SettleNativeAttempt(child.JobId, claim.Correlation, "receipt"));
        Assert.False(restarted.SettleNativeAttempt(child.JobId, claim.Correlation, "duplicate"));
        restarted.RecordNativeSubmission(child.JobId, claim.Correlation, "late-submission");
        Assert.Equal("receipt", restarted.GetJob(child.JobId)!.ResultText);
        Assert.Equal("submission-1", restarted.NativeSubmissionId(child.JobId));
    }

    [Fact]
    public async Task Native_user_receipt_releases_fence_before_model_completion()
    {
        using var fixture = new JobFixture();
        var backend = new ScriptedBackend(request =>
        [
            new BackendEvidence.Session(request.Correlation, "thread-receipt"),
            new BackendEvidence.Result(request.Correlation, "done")
        ]);
        var catalog = Catalog(backend);
        var parent = Accept(fixture, catalog).Execute(new SubmitJobRequest("parent", "work", null, false) { Backend = BackendCatalog.Codex }).Job!;
        using var dispatcher = new DispatchJob(fixture.Store, catalog, fixture.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        var follow = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog));
        var first = follow.Execute(new FollowUpRequest(parent.JobId, "first", "first")).Job!;
        var claim = fixture.Store.BeginNativeCodexAttempt(_ => true, "/tmp/codex-home")!;
        fixture.Store.RecordNativeReceipt(first.JobId, claim.Correlation);
        Assert.Equal(JobStatus.Running, fixture.Store.GetJob(first.JobId)!.Status);
        Assert.True(fixture.Store.GetRuns(first.JobId).Single().Acked);
        var second = follow.Execute(new FollowUpRequest(parent.JobId, "second", "second")).Job!;
        Assert.Equal(second.JobId, fixture.Store.BeginNativeCodexAttempt(_ => true, "/tmp/codex-home")!.Job.JobId);
        Assert.True(fixture.Store.SettleNativeAttempt(first.JobId, claim.Correlation, "first result"));
        Assert.Equal("first result", fixture.Store.GetJob(first.JobId)!.ResultText);
    }

    [Fact]
    public async Task Large_codex_follow_up_uses_resume()
    {
        using var fixture = new JobFixture();
        var backend = new ScriptedBackend(request =>
        [
            new BackendEvidence.Session(request.Correlation, "thread-large"),
            new BackendEvidence.Result(request.Correlation, "done")
        ]);
        var catalog = Catalog(backend);
        var parent = Accept(fixture, catalog).Execute(new SubmitJobRequest("parent", "work", null, false) { Backend = BackendCatalog.Codex }).Job!;
        using var dispatcher = new DispatchJob(fixture.Store, catalog, fixture.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        var child = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog))
            .Execute(new FollowUpRequest(parent.JobId, new string('x', 17_000), "large")).Job!;
        Assert.DoesNotContain("native_codex", fixture.Store.GetJob(child.JobId)!.Options);
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        Assert.Equal("thread-large", backend.Started[1].ResumeSessionId);
    }

    [Fact]
    public async Task Unverifiable_codex_thread_falls_back_to_resume()
    {
        using var fixture = new JobFixture();
        var backend = new ScriptedBackend(request =>
        [
            new BackendEvidence.Session(request.Correlation, "thread-dead"),
            new BackendEvidence.Result(request.Correlation, "done")
        ]);
        var catalog = Catalog(backend);
        var parent = Accept(fixture, catalog).Execute(new SubmitJobRequest("parent", "work", null, false) { Backend = BackendCatalog.Codex }).Job!;
        using var dispatcher = new DispatchJob(fixture.Store, catalog, fixture.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        var child = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog))
            .Execute(new FollowUpRequest(parent.JobId, "next", "dead")).Job!;
        Assert.Null(fixture.Store.BeginNativeCodexAttempt(_ => false, "/tmp/codex-home"));
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        Assert.Equal("thread-dead", backend.Started[1].ResumeSessionId);
        Assert.Null(fixture.Store.NativeSubmissionId(child.JobId));
    }
}
