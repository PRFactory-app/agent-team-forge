using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class ManagedParityTests
{
    [Fact]
    public async Task Deferred_turn_waits_for_parent_and_survives_store_restart_exactly_once()
    {
        using var f = new JobFixture();
        var parent = f.Submit("first");
        var follow = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept());
        var request = new FollowUpRequest(parent.JobId, "next", "second") { Defer = true };
        var child = follow.Execute(request).Job!;
        Assert.Equal(child.JobId, follow.Execute(request).Job!.JobId);
        var parentClaim = f.Store.BeginNextAttempt()!;
        Assert.Equal(parent.JobId, parentClaim.Job.JobId);
        Assert.Null(f.Store.BeginNextAttempt());
        var run = new RunRef(parent.JobId, parentClaim.RunId, parentClaim.Generation, parentClaim.Correlation);
        f.Store.RecordSession(run, "native-session");
        Assert.Null(f.Store.BeginNextAttempt());
        Assert.Equal(JobStatus.Running, f.Store.GetJob(parent.JobId)!.Status);
        f.Store.Complete(run, "done");
        Assert.Null(f.Store.BeginNextAttempt([parent.JobId])); // Dispatcher still disposing the parent.

        // New store/dispatcher over the committed database: no in-memory delivery queue.
        var restarted = f.NewStore();
        Assert.Empty(restarted.QuarantineUncertainAttempts());
        var backend = new ScriptedBackend(r =>
        [
            new BackendEvidence.Session(r.Correlation, r.ResumeSessionId!),
            new BackendEvidence.Result(r.Correlation, "continued")
        ]);
        using var dispatcher = new DispatchJob(restarted, backend, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        await dispatcher.RunAttemptAsync(restarted.BeginNextAttempt()!, TestContext.Current.CancellationToken);
        Assert.Equal("native-session", backend.Started.Single().ResumeSessionId);
        Assert.Null(restarted.BeginNextAttempt());
        Assert.Equal("existing", follow.Execute(request).Outcome);
        Assert.Equal(1, restarted.GetJob(child.JobId)!.Attempts);
    }

    [Fact]
    public void Restart_during_parent_turn_reports_deferred_work_as_blocked_without_dispatching_it()
    {
        using var f = new JobFixture();
        var parent = f.Submit("first");
        var claim = f.Store.BeginNextAttempt()!;
        f.Store.RecordSession(new RunRef(parent.JobId, claim.RunId, claim.Generation, claim.Correlation), "native");
        var child = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept())
            .Execute(new FollowUpRequest(parent.JobId, "next", "second") { Defer = true }).Job!;
        var restarted = f.NewStore();
        Assert.Equal([parent.JobId], restarted.QuarantineUncertainAttempts());
        Assert.Null(restarted.BeginNextAttempt());
        var retry = new FollowUpJob(restarted, JobFixture.Operator, f.Accept(restarted));
        var request = new FollowUpRequest(parent.JobId, "next", "second") { Defer = true };
        Assert.Equal(child.JobId, retry.Execute(request).Job!.JobId);
        Assert.Equal(JobErrors.IdempotencyConflict, retry.Execute(request with { Instruction = "different" }).Error);
        var view = new GetJob(restarted, JobFixture.Operator).Execute(child.JobId).Job!;
        Assert.Equal((JobStatus.Queued, "parent_needs_reconciliation", 0), (view.Status, view.ReasonCode, view.Attempts));
    }

    [Fact]
    public async Task Interrupt_without_follow_up_preserves_session_for_revive()
    {
        using var f = new JobFixture();
        var parent = f.Submit("first");
        var busy = new ScriptedBackend(r => [new BackendEvidence.Session(r.Correlation, "native")]) { Hangs = true };
        using var dispatcher = new DispatchJob(f.Store, busy, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        var attempt = dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        await Bounded.Until(() => f.Store.GetJob(parent.JobId)!.SessionId is not null, "session");
        var stop = new StopJob(f.Store, JobFixture.Operator, dispatcher.CancelRunning, interruptRunning: dispatcher.InterruptRunning);
        Assert.Equal("interrupted", stop.Execute(parent.JobId, true).Job!.ReasonCode);
        await attempt.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
        Assert.Equal(0, f.Store.CountUnattemptedIntents());
        Assert.Equal(1, busy.Terminations);
        var revived = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept())
            .Execute(new FollowUpRequest(parent.JobId, "continue", "revive")).Job!;
        var resumed = new ScriptedBackend(r =>
        [
            new BackendEvidence.Session(r.Correlation, r.ResumeSessionId!),
            new BackendEvidence.Result(r.Correlation, "done")
        ]);
        using var next = new DispatchJob(f.Store, resumed, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        await next.RunAttemptAsync(f.Store.BeginNextAttempt()!, TestContext.Current.CancellationToken);
        Assert.Equal("native", resumed.Started.Single().ResumeSessionId);
        Assert.Equal(JobStatus.Completed, f.Store.GetJob(revived.JobId)!.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Replace_opt_out_refuses_live_idle_sessions_but_revives_dead_sessions(bool live)
    {
        using var f = new JobFixture();
        var parent = f.Submit("first");
        var claim = f.Store.BeginNextAttempt()!;
        var run = new RunRef(parent.JobId, claim.RunId, claim.Generation, claim.Correlation);
        f.Store.RecordSession(run, "native");
        f.Store.Complete(run, "done");
        var child = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept())
            .Execute(new FollowUpRequest(parent.JobId, "next", "second") { ReplaceIfIdle = false }).Job!;
        var backend = new IdleBackend(live);
        using var dispatcher = new DispatchJob(f.Store, new BackendCatalog().Register("fake", () => backend),
            f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        await dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, TestContext.Current.CancellationToken);
        var result = f.Store.GetJob(child.JobId)!;
        Assert.Equal(live ? "agent_idle_but_alive" : null, result.ReasonCode);
        Assert.Equal(live ? JobStatus.Failed : JobStatus.Completed, result.Status);
        Assert.Equal(live ? 0 : 1, backend.Agent.Started.Count);
    }

    [Fact]
    public void Delivery_evidence_survives_restart_and_cancellation()
    {
        using var f = new JobFixture();
        var parent = f.Submit("receipt");
        Assert.Equal("pending", f.Get().Execute(parent.JobId).Job!.Delivery!.State);
        var claim = f.Store.BeginNextAttempt()!;
        Assert.Equal("unconfirmed", f.Get().Execute(parent.JobId).Job!.Delivery!.State);
        var run = new RunRef(parent.JobId, claim.RunId, claim.Generation, claim.Correlation);
        f.Store.RecordBackendEvidence(run, null, acked: true);
        f.Store.Cancel(parent.JobId, JobFixture.Operator.Principal, JobFixture.Operator.Team);
        var receipt = new GetJob(f.NewStore(), JobFixture.Operator).Execute(parent.JobId).Job!.Delivery!;
        Assert.Equal("acknowledged", receipt.State);
        Assert.Equal(claim.RunId, receipt.RunId);
        Assert.NotNull(receipt.AcknowledgedAt);
    }

    [Fact]
    public void Expected_paths_are_durable_and_part_of_idempotency()
    {
        using var f = new JobFixture();
        var request = new SubmitJobRequest("paths", "write files", null, false) { ExpectedOutputs = ["one;two=three", "/tmp/å.txt"] };
        var accepted = f.Accept().Execute(request);
        Assert.Equal(request.ExpectedOutputs, new GetJob(f.NewStore(), JobFixture.Operator).Execute(accepted.Job!.JobId).Job!.ExpectedOutputs);
        Assert.Equal(JobErrors.IdempotencyConflict, f.Accept().Execute(request with { ExpectedOutputs = ["different"] }).Error);
    }

    sealed class IdleBackend(bool live) : IJobBackend, IInteractiveSessionStop
    {
        public ScriptedBackend Agent { get; } = new(r => [new BackendEvidence.Result(r.Correlation, "done")]);
        public IBackendRun Start(BackendRequest request) => Agent.Start(request);
        public bool HasIdleSession(string sessionId) => live;
        public bool StopIdleSession(string sessionId) => throw new InvalidOperationException("must not close idle agent");
        public void StopAllIdleSessions() { }
    }
}
