using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class ReviewTurnQueueTests
{
    [Fact]
    public void Claude_received_turn_holds_admission_and_claim_until_settled()
    {
        using var f = new JobFixture();
        var catalog = new BackendCatalog().Register(BackendCatalog.Claude, () => new ScriptedBackend(_ => []));
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, f.TestProfile, f.Admission, catalog.Names);
        var root = accept.Execute(new SubmitJobRequest("root", "work", null, false) { Backend = BackendCatalog.Claude }).Job!;
        var rootClaim = f.Store.BeginNextAttempt()!;
        var rootRun = new RunRef(root.JobId, rootClaim.RunId, 1, rootClaim.Correlation);
        f.Store.RecordSession(rootRun, "claude-review");
        f.Store.Complete(rootRun, "ready");
        var follow = new FollowUpJob(f.Store, JobFixture.Operator, accept);
        var first = follow.Execute(new FollowUpRequest(root.JobId, "first", "first") { Defer = true }).Job!;
        var claim = f.Store.BeginNativeClaudeAttempt(root.JobId, Path.GetTempPath(), _ => true)!;
        f.Store.RecordNativeClaudeReceipt(first.JobId, claim.Correlation);
        Assert.Equal(JobErrors.ParentNotReady, follow.Execute(new FollowUpRequest(root.JobId, "refused", "refused") { Defer = false }).Error);
        var next = follow.Execute(new FollowUpRequest(root.JobId, "next", "next") { Defer = true }).Job!;
        // A carrier can finish before the independently observed native turn does.
        f.Store.Complete(new RunRef(first.JobId, claim.RunId, 1, claim.Correlation), "carrier ended");
        Assert.Null(f.Store.BeginNativeClaudeAttempt(root.JobId, Path.GetTempPath(), _ => true));
        Assert.True(f.Store.SettleNativeClaudeAttempt(first.JobId, claim.Correlation, "turn ended"));
        Assert.Equal(next.JobId, f.Store.BeginNativeClaudeAttempt(root.JobId, Path.GetTempPath(), _ => true)!.Job.JobId);
    }

    [Theory]
    [InlineData("{\"type\":\"event_msg\",\"payload\":{\"type\":\"turn_aborted\",\"reason\":\"interrupted\"}}")]
    [InlineData("{\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"human takes over\"}}")]
    public void Interrupted_native_turn_releases_queue(string ending) => NativeEnding(ending, JobStatus.Cancelled, "interactive_turn_interrupted");

    [Theory]
    [InlineData(BackendCatalog.Codex)]
    [InlineData(BackendCatalog.Claude)]
    public void Api_error_followed_by_completion_fails_and_releases_queue(string backend) => NativeEnding(
        backend == BackendCatalog.Codex
            ? """{"type":"event_msg","payload":{"type":"task_complete","error":{"message":"server failed"}}}"""
            : """{"type":"assistant","isSidechain":false,"sessionId":"review-thread","isApiErrorMessage":true,"message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"API Error: server failed"}]}}""",
        JobStatus.Failed, "agent_api_error", backend);

    static void NativeEnding(string ending, string expectedStatus, string reason, string backend = BackendCatalog.Codex)
    {
        using var f = new JobFixture();
        using var home = new TempStateDir();
        var catalog = new BackendCatalog().Register(backend, () => new ScriptedBackend(_ => []));
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, f.TestProfile, f.Admission, catalog.Names);
        var root = accept.Execute(new SubmitJobRequest("root", "work", null, false) { Backend = backend }).Job!;
        var rootClaim = f.Store.BeginNextAttempt()!;
        var rootRun = new RunRef(root.JobId, rootClaim.RunId, 1, rootClaim.Correlation);
        f.Store.RecordSession(rootRun, "review-thread");
        f.Store.Complete(rootRun, "ready");
        var follow = new FollowUpJob(f.Store, JobFixture.Operator, accept);
        var first = follow.Execute(new FollowUpRequest(root.JobId, "first", "first") { Defer = true }).Job!;
        var claim = backend == BackendCatalog.Codex
            ? f.Store.BeginNativeCodexAttempt(_ => true, home.Path)!
            : f.Store.BeginNativeClaudeAttempt(root.JobId, home.Path, _ => true)!;
        if (backend == BackendCatalog.Codex) { f.Store.RecordNativeReceipt(first.JobId, claim.Correlation); }
        else { f.Store.RecordNativeClaudeReceipt(first.JobId, claim.Correlation); }
        Assert.Equal(JobErrors.ParentNotReady, follow.Execute(new FollowUpRequest(root.JobId, "refused", "refused") { Defer = false }).Error);
        var next = follow.Execute(new FollowUpRequest(root.JobId, "next", "next") { Defer = true }).Job!;
        var dir = Directory.CreateDirectory(Path.Combine(home.Path, backend == BackendCatalog.Codex ? "sessions" : "projects"));
        File.WriteAllLines(Path.Combine(dir.FullName, backend == BackendCatalog.Codex ? "rollout-review-review-thread.jsonl" : "review-thread.jsonl"), backend == BackendCatalog.Codex ?
        [
            """{"type":"session_meta","payload":{"id":"review-thread","source":"cli"}}""",
            $$$"""{"type":"event_msg","payload":{"type":"user_message","message":"atf-corr:{{{claim.Correlation}}}"}}""",
            ending
        ] :
        [
            $$$"""{"type":"user","isSidechain":false,"sessionId":"review-thread","message":{"role":"user","content":"atf-corr:{{{claim.Correlation}}}"}}""",
            ending
        ]);
        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        Assert.True(dispatcher.SettleCompletedInteractive(f.Store.GetJob(first.JobId)!));
        Assert.Equal(expectedStatus, f.Store.GetJob(first.JobId)!.Status);
        Assert.Equal(reason, f.Store.GetJob(first.JobId)!.ReasonCode);
        if (expectedStatus == JobStatus.Failed) { Assert.Contains("server failed", f.Store.GetJob(first.JobId)!.ResultText); }
        Assert.Equal("settled", backend == BackendCatalog.Codex ? f.Store.NativeAttempt(first.JobId)!.State : f.Store.NativeClaudeAttempt(first.JobId)!.State);
        Assert.False(backend == BackendCatalog.Codex ? f.Store.SettleNativeAttempt(first.JobId, claim.Correlation, "duplicate") : f.Store.SettleNativeClaudeAttempt(first.JobId, claim.Correlation, "duplicate"));
        var restarted = f.NewStore();
        var nextClaim = backend == BackendCatalog.Codex ? restarted.BeginNativeCodexAttempt(_ => true, home.Path) : restarted.BeginNativeClaudeAttempt(root.JobId, home.Path, _ => true);
        Assert.Equal(next.JobId, nextClaim!.Job.JobId);
    }

    [Fact]
    public void Stopping_middle_turn_cancels_only_semantic_descendants_and_preserves_tail_order()
    {
        using var f = new JobFixture();
        var root = f.Submit("root");
        var rootClaim = f.Store.BeginNextAttempt()!;
        var rootRun = new RunRef(root.JobId, rootClaim.RunId, 1, rootClaim.Correlation);
        f.Store.RecordSession(rootRun, "root-session");
        var follow = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept());
        var middle = follow.Execute(new FollowUpRequest(root.JobId, "middle", "middle") { Defer = true }).Job!;
        var tail = follow.Execute(new FollowUpRequest(root.JobId, "independent", "tail") { Defer = true }).Job!;
        var child = follow.Execute(new FollowUpRequest(middle.JobId, "child", "child") { Defer = true }).Job!;
        var grandchild = follow.Execute(new FollowUpRequest(child.JobId, "grandchild", "grandchild") { Defer = true }).Job!;
        var last = follow.Execute(new FollowUpRequest(root.JobId, "last", "last") { Defer = true }).Job!;
        f.Store.Cancel(middle.JobId, JobFixture.Operator.Principal, JobFixture.Operator.Team);
        Assert.Equal(JobStatus.Queued, f.Store.GetJob(tail.JobId)!.Status);
        Assert.Equal(JobStatus.Queued, f.Store.GetJob(last.JobId)!.Status);
        Assert.Equal(JobStatus.Cancelled, f.Store.GetJob(child.JobId)!.Status);
        Assert.Equal(JobStatus.Cancelled, f.Store.GetJob(grandchild.JobId)!.Status);
        f.Store.Complete(rootRun, "ready");
        var restarted = f.NewStore();
        foreach (var id in (string[])[tail.JobId, last.JobId])
        {
            var claim = restarted.BeginNextAttempt()!;
            Assert.Equal(id, claim.Job.JobId);
            restarted.RecordSession(new RunRef(id, claim.RunId, 1, claim.Correlation), "root-session");
            restarted.Complete(new RunRef(id, claim.RunId, 1, claim.Correlation), "done");
        }
        Assert.Equal(6, new PruneJobs(f.Database).Execute(DateTimeOffset.UtcNow.AddMinutes(1), false).Count);
    }
}
