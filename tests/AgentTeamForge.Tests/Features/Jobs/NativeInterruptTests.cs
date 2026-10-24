using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class NativeInterruptTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Native_child_in_needs_reconciliation_stop_never_closes_pane(bool codex, bool interrupt)
    {
        foreach (var reason in new[] { "native_submission_unresolved", "native_delivery_unresolved", "native_interrupt_unconfirmed" })
        {
            using var f = await NativeInterruptFixture.Create(codex);
            f.Quarantine(reason);
            var stop = new StopJob(f.Store, JobFixture.Operator, _ => Assert.Fail("terminate callback"),
                _ => Assert.Fail("queued close callback"), _ => throw new InvalidOperationException("reconciled close callback"),
                interruptRunning: _ => Assert.Fail("ordinary interrupt callback"), releaseNative: _ => throw new InvalidOperationException("implicit release"));
            Assert.Equal("native_interrupt_pending", stop.Execute(f.Child, interrupt).Outcome);
            Assert.Equal("native_interrupt_pending", stop.Execute(f.Child, interrupt).Outcome);
            Assert.Equal(JobStatus.NeedsReconciliation, f.Store.GetJob(f.Child)!.Status);
            Assert.Single(f.Store.GetEvents(f.Child), e => e.Kind is "native_interrupt_requested" or "native_turn_interrupt_requested");
            Assert.Equal(0, f.Control.Closes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_stop_without_confirmation_keeps_chain_fenced(bool codex)
    {
        using var f = await NativeInterruptFixture.Create(codex);
        f.Stop.Execute(f.Child);
        await f.Until(() => f.Control.Escapes == 1);
        await f.Until(() => f.Store.GetJob(f.Child)!.ReasonCode == "native_interrupt_unconfirmed");
        Assert.Equal("native_interrupt_unconfirmed", f.Stop.Execute(f.Child).Outcome);
        Assert.NotNull(f.Store.PendingNativeInterrupt(f.Child));
        Assert.True(f.Store.IsSessionFenced(f.Child));
        Assert.Equal(JobErrors.ParentNotReady, f.Follow.Execute(new(f.Parent, "other", "blocked")).Error);
        Assert.Equal(1, f.Control.Escapes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Confirmed_native_stop_cancels_deferred_children_interrupt_keeps_them(bool codex, bool interrupt)
    {
        using var f = await NativeInterruptFixture.Create(codex);
        var descendant = f.Follow.Execute(new(f.Child, "descendant", "descendant") { Defer = true }).Job!;
        var unrelated = f.Follow.Execute(new(f.Parent, "earlier address", "unrelated") { Defer = true }).Job!;
        Assert.Equal("native_interrupt_pending", f.Stop.Execute(f.Child, interrupt).Outcome);
        Assert.Equal(JobStatus.Queued, f.Store.GetJob(descendant.JobId)!.Status);
        Assert.Null(codex ? f.Store.BeginNativeCodexAttempt(_ => true, f.Home) : f.Store.BeginNativeClaudeAttempt(f.Parent, f.Home, _ => true));
        f.Confirm();
        await f.Until(() => f.Store.GetJob(f.Child)!.Status == JobStatus.Cancelled);
        Assert.Equal(interrupt ? JobStatus.Queued : JobStatus.Cancelled, f.Store.GetJob(descendant.JobId)!.Status);
        Assert.Equal(JobStatus.Queued, f.Store.GetJob(unrelated.JobId)!.Status);
        Assert.Null(f.Store.PendingNativeInterrupt(f.Child));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Native_stop_racing_natural_completion_keeps_real_result(bool codex, bool endBeforeRequest)
    {
        using var f = await NativeInterruptFixture.Create(codex);
        var child = f.Follow.Execute(new(f.Child, "later", "deferred") { Defer = true }).Job!;
        if (endBeforeRequest) { f.Complete(); }
        f.Stop.Execute(f.Child);
        if (!endBeforeRequest) { f.Complete(); }
        await f.Until(() => f.Store.GetJob(f.Child)!.Status == JobStatus.Completed);
        Assert.Equal("actual result", f.Store.GetJob(f.Child)!.ResultText);
        Assert.Equal(JobStatus.Cancelled, f.Store.GetJob(child.JobId)!.Status);
        Assert.Null(f.Store.PendingNativeInterrupt(f.Child));
        Assert.False(f.Store.IsSessionFenced(f.Child));
        var response = Endpoint(f).Handle(new() { Op = IpcProtocol.JobStop, JobId = f.Child });
        Assert.True(response.Ok);
        Assert.Equal("actual result", Endpoint(f).Handle(new() { Op = IpcProtocol.JobGet, JobId = f.Child }).Job!.Result);
        Assert.Contains("actual result", ((TextContentBlock)JobsMcpBridge.ToolResult(response).Content[0]).Text);
        Assert.Contains("after the turn ended", response.ErrorDetail);
        Assert.Equal(0, f.Control.Escapes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_stop_racing_native_api_failure_keeps_actual_failure(bool codex)
    {
        using var f = await NativeInterruptFixture.Create(codex);
        f.Stop.Execute(f.Child);
        File.AppendAllLines(f.Transcript, codex
            ? ["""{"type":"event_msg","payload":{"type":"task_complete","turn_id":"target-turn","error":{"message":"Selected model is at capacity"}}}"""]
            : [$$$"""{"type":"assistant","sessionId":"{{{f.Session}}}","isApiErrorMessage":true,"message":{"role":"assistant","stop_reason":"stop_sequence","content":[{"type":"text","text":"API Error: Connection lost mid-response."}]}}""",
                $$$"""{"type":"system","sessionId":"{{{f.Session}}}","subtype":"turn_duration","durationMs":1000}"""]);
        await f.Until(() => f.Store.GetJob(f.Child)!.Status == JobStatus.Failed);
        Assert.Equal("agent_api_error", f.Store.GetJob(f.Child)!.ReasonCode);
        Assert.Null(f.Store.PendingNativeInterrupt(f.Child));
        Assert.False(f.Store.IsSessionFenced(f.Child));
        Assert.Equal(0, f.Control.Escapes);
        Assert.Equal(0, f.Control.Closes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_native_stop_does_not_hold_ipc_claim_pause(bool codex)
    {
        using var f = await NativeInterruptFixture.Create(codex);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Control.BeforeKey = async () => { entered.TrySetResult(); await release.Task; };
        try
        {
            using (f.Dispatcher.PauseClaims()) { Assert.Equal("native_interrupt_pending", f.Stop.Execute(f.Child).Outcome); }
            f.Reconcile();
            await entered.Task.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
            var other = f.Accept.Execute(new("other", "unrelated", null, false) { Backend = codex ? "codex" : "claude", TargetAgent = "other" }).Job!;
            await Task.Run(() =>
            {
                using var pause = f.Dispatcher.PauseClaims();
                Assert.Equal(other.JobId, f.Store.BeginNextAttempt()!.Job.JobId);
            }, TestContext.Current.CancellationToken).WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
            Assert.NotEqual(JobStatus.Cancelled, f.Store.GetJob(f.Child)!.Status);
        }
        finally { release.TrySetResult(); }
        await f.Until(() => f.Control.Escapes == 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Escape_after_target_changed_reports_misdirected_not_stopped(bool codex)
    {
        using var f = await NativeInterruptFixture.Create(codex);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Control.AfterValidation = async () => { entered.SetResult(); await release.Task; };
        try
        {
            f.Stop.Execute(f.Child);
            f.Reconcile();
            await entered.Task.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
            f.HumanInput();
        }
        finally { release.TrySetResult(); }
        await f.Until(() => f.Control.Escapes == 1);
        f.Confirm();
        await f.Until(() => f.Store.GetJob(f.Child)!.ReasonCode == "native_interrupt_misdirected");
        Assert.NotEqual(JobStatus.Cancelled, f.Store.GetJob(f.Child)!.Status);
        Assert.True(f.Store.IsSessionFenced(f.Child));
        Assert.Equal(1, f.Control.Escapes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_resumes_requested_native_interrupt_without_prompt_replay(bool codex)
    {
        using var f = await NativeInterruptFixture.Create(codex);
        f.Stop.Execute(f.Child);
        var restarted = f.Jobs.NewStore();
        using var dispatcher = new DispatchJob(restarted, f.Catalog, f.Jobs.Limits, DurabilityCheckpoints.None, f.Jobs.Admission, _ => { });
        restarted.QuarantineUncertainAttempts();
        var run = restarted.GetRuns(f.Child)[^1];
        Assert.True(restarted.ReattachQuarantined(new(f.Child, run.RunId, run.Generation, run.Correlation), f.Parent));
        Assert.True(restarted.IsSessionFenced(f.Child));
        await Bounded.Until(() =>
        {
            if (codex) { dispatcher.ReconcileNativeCodex(); } else { dispatcher.ReconcileNativeClaude(); }
            return f.Control.Escapes == 1;
        }, "restarted interrupt dispatch");
        Assert.Equal(1, f.Control.Starts);
        f.Confirm();
        await f.Until(() => restarted.GetJob(f.Child)!.Status == JobStatus.Cancelled);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Restart_with_sent_or_ambiguous_dispatch_never_resends_escape(bool codex, bool sent)
    {
        using var f = await NativeInterruptFixture.Create(codex);
        f.Stop.Execute(f.Child);
        var intent = f.Store.PendingNativeInterrupt(f.Child)!;
        Assert.True(f.Store.AdvanceNativeInterrupt(f.Child, intent.RunId, intent.Phase, "native_interrupt_dispatching"));
        if (sent) { Assert.True(f.Store.AdvanceNativeInterrupt(f.Child, intent.RunId, "native_interrupt_dispatching", "native_interrupt_sent")); }
        using var dispatcher = new DispatchJob(f.Jobs.NewStore(), f.Catalog, f.Jobs.Limits, DurabilityCheckpoints.None, f.Jobs.Admission, _ => { });
        await Bounded.Until(() =>
        {
            if (codex) { dispatcher.ReconcileNativeCodex(); } else { dispatcher.ReconcileNativeClaude(); }
            return f.Store.GetJob(f.Child)!.ReasonCode == "native_interrupt_unconfirmed";
        }, "restarted observation only");
        Assert.Equal(0, f.Control.Escapes);
        Assert.True(f.Store.IsSessionFenced(f.Child));
        f.Confirm();
        await f.Until(() => f.Store.GetJob(f.Child)!.Status == JobStatus.Cancelled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_native_child_stop_cancels_without_escaping_parent(bool codex)
    {
        using var f = await NativeInterruptFixture.Create(codex, claimChild: false);
        Assert.Equal("stopped", f.Stop.Execute(f.Child).Outcome);
        Assert.Equal(JobStatus.Cancelled, f.Store.GetJob(f.Child)!.Status);
        Assert.Empty(f.Store.GetRuns(f.Child));
        Assert.Null(codex ? f.Store.BeginNativeCodexAttempt(_ => true, f.Home) : f.Store.BeginNativeClaudeAttempt(f.Parent, f.Home, _ => true));
        Assert.Equal(0, f.Control.Escapes);
        Assert.Equal(0, f.Control.Closes);
    }

    [Fact]
    public async Task Native_stop_with_unstarted_cli_queue_remains_unconfirmed()
    {
        using var f = await NativeInterruptFixture.Create(true);
        f.WriteInput(started: false);
        f.Stop.Execute(f.Child);
        await f.Until(() => f.Store.GetJob(f.Child)!.ReasonCode == "native_interrupt_unconfirmed");
        Assert.Equal(0, f.Control.Escapes);
        Assert.True(f.Store.NativeAttempt(f.Child)!.Unresolved);
        f.WriteInput();
        await f.Until(() => f.Control.Escapes == 1);
        f.Confirm();
        await f.Until(() => f.Store.GetJob(f.Child)!.Status == JobStatus.Cancelled);
    }

    [Fact]
    public async Task Claude_queue_dequeue_diagnoses_delivery_without_proving_execution()
    {
        using var f = await NativeInterruptFixture.Create();
        File.WriteAllLines(f.Transcript,
        [
            $$$"""{"type":"user","sessionId":"{{{f.Session}}}","isSidechain":false,"message":{"role":"user","content":"earlier turn"}}""",
            $$$"""{"type":"queue-operation","sessionId":"{{{f.Session}}}","operation":"enqueue","content":"atf-corr:{{{f.Correlation}}}"}""",
            $$$"""{"type":"queue-operation","sessionId":"{{{f.Session}}}","operation":"dequeue"}"""
        ]);
        f.Stop.Execute(f.Child);
        await f.Until(() => f.Store.GetJob(f.Child)!.ReasonCode == "native_interrupt_unconfirmed");
        Assert.Contains(f.Store.GetEvents(f.Child), e => e.Kind == "native_delivery_dequeued" && e.RunId == f.Store.PendingNativeInterrupt(f.Child)!.RunId);
        Assert.Equal("posting", f.Store.NativeClaudeAttempt(f.Child)!.State);
        Assert.Equal(0, f.Control.Escapes);
    }

    [Fact]
    public async Task Claude_input_dequeued_after_confirmation_keeps_ownership_fenced()
    {
        using var f = await NativeInterruptFixture.Create();
        f.Stop.Execute(f.Child);
        f.Confirm();
        File.AppendAllLines(f.Transcript, [$$$"""{"type":"queue-operation","sessionId":"{{{f.Session}}}","operation":"dequeue"}"""]);
        await f.Until(() => f.Store.GetJob(f.Child)!.ReasonCode == "native_interrupt_misdirected");
        Assert.True(f.Store.IsSessionFenced(f.Child));
        Assert.NotEqual(JobStatus.Cancelled, f.Store.GetJob(f.Child)!.Status);
        Assert.NotNull(f.Store.PendingNativeInterrupt(f.Child));
    }

    [Fact]
    public async Task Codex_stop_during_submit_preserves_delivery_uncertainty()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submits = 0;
        using var f = await NativeInterruptFixture.Create(true, submit: async (_, _, _, _) =>
        {
            Interlocked.Increment(ref submits);
            entered.SetResult();
            await release.Task;
            return new CodexSubmission(false, null);
        });
        File.Delete(f.Transcript);
        var run = f.Store.GetRuns(f.Child)[^1];
        var running = f.Dispatcher.RunAttemptAsync(new(f.Store.GetJob(f.Child)!, run.RunId, run.Generation, run.Correlation), CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
            Assert.Equal("native_interrupt_pending", f.Stop.Execute(f.Child).Outcome);
        }
        finally { release.TrySetResult(); }
        await running.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
        Assert.True(f.Store.NativeAttempt(f.Child)!.Unresolved);
        Assert.NotEqual(JobStatus.Queued, f.Store.GetJob(f.Child)!.Status);
        Assert.NotNull(f.Store.PendingNativeInterrupt(f.Child));
        Assert.DoesNotContain(f.Store.GetEvents(f.Child), e => e.Kind == "native_reverted");
        Assert.Equal(1, submits);
    }

    [Fact]
    public async Task Claude_late_settlement_cannot_overwrite_confirmed_stop()
    {
        using var f = await NativeInterruptFixture.Create();
        f.Stop.Execute(f.Child);
        f.Confirm();
        await f.Until(() => f.Store.GetJob(f.Child)!.Status == JobStatus.Cancelled);
        f.Store.RecordNativeClaudePost(f.Child, f.Correlation);
        f.Store.RecordNativeClaudeReceipt(f.Child, f.Correlation);
        Assert.False(f.Store.SettleNativeClaudeAttempt(f.Child, f.Correlation, "late"));
        Assert.Equal("stopped", f.Store.GetJob(f.Child)!.ReasonCode);
        Assert.Equal("settled", f.Store.NativeClaudeAttempt(f.Child)!.State);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Native_stop_sql_fence_blocks_racing_claude_and_codex_claims(bool codex, bool claimFirst)
    {
        using var f = await NativeInterruptFixture.Create(codex, claimChild: false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var calls = 0;
        bool ClaimCallback()
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            release.Wait(TestContext.Current.CancellationToken);
            return true;
        }
        AttemptClaim? Claim() => codex ? f.Store.BeginNativeCodexAttempt(_ => ClaimCallback(), f.Home)
            : f.Store.BeginNativeClaudeAttempt(f.Parent, f.Home, _ => ClaimCallback());
        if (!claimFirst)
        {
            Assert.Equal("stopped", f.Stop.Execute(f.Child).Outcome);
            Assert.Null(Claim());
            Assert.Equal(0, calls);
            return;
        }
        var claiming = Task.Run(Claim);
        try
        {
            await entered.Task.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
            var stopping = Task.Run(() => f.Stop.Execute(f.Child));
            release.Set();
            var claim = await claiming.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
            Assert.NotNull(claim);
            Assert.Equal("native_interrupt_pending", (await stopping).Outcome);
            Assert.True(f.Store.IsSessionFenced(f.Child));
            Assert.Equal(claim.RunId, f.Store.PendingNativeInterrupt(f.Child)!.RunId);
        }
        finally { release.Set(); await claiming; }
    }

    [Theory]
    [InlineData(false, "prior")]
    [InlineData(true, "prior")]
    [InlineData(false, "intervening")]
    [InlineData(true, "intervening")]
    [InlineData(false, "foreign")]
    public async Task Arbitrary_user_or_quoted_sentinel_does_not_confirm_native_stop(bool codex, string shape)
    {
        using var f = await NativeInterruptFixture.Create(codex);
        f.Stop.Execute(f.Child);
        if (shape == "prior") { f.Confirm(DateTimeOffset.UtcNow.AddHours(-1).ToString("O")); }
        else if (shape == "foreign")
        {
            File.AppendAllLines(f.Transcript, ["""{"type":"user","sessionId":"wrong-session","message":{"role":"user","content":"[Request interrupted by user]"}}"""]);
        }
        else { f.HumanInput(); f.Confirm(); }
        await f.Until(() => f.Store.GetJob(f.Child)!.ReasonCode is "native_interrupt_unconfirmed" or "native_interrupt_misdirected");
        Assert.NotEqual(JobStatus.Cancelled, f.Store.GetJob(f.Child)!.Status);
        Assert.NotNull(f.Store.PendingNativeInterrupt(f.Child));
        Assert.True(f.Store.IsSessionFenced(f.Child));
    }

    [Fact]
    public async Task Matching_codex_turn_aborted_confirms_native_interrupt()
    {
        using var f = await NativeInterruptFixture.Create(true);
        f.Stop.Execute(f.Child, true);
        f.Confirm(turn: "another-turn");
        await f.Until(() => f.Store.GetJob(f.Child)!.ReasonCode == "native_interrupt_unconfirmed");
        Assert.NotEqual(JobStatus.Cancelled, f.Store.GetJob(f.Child)!.Status);
        f.Confirm();
        await f.Until(() => f.Store.GetJob(f.Child)!.Status == JobStatus.Cancelled);
        Assert.Equal("interrupted", f.Store.GetJob(f.Child)!.ReasonCode);
    }

    [Theory]
    [InlineData("another-turn")]
    [InlineData(null)]
    public async Task Codex_unmatched_completion_cannot_settle_pending_stop(string? turn)
    {
        using var f = await NativeInterruptFixture.Create(true);
        f.Stop.Execute(f.Child);
        var turnField = turn is null ? "" : ",\"turn_id\":\"" + turn + "\"";
        File.AppendAllLines(f.Transcript,
            ["{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"last_agent_message\":\"wrong result\",\"error\":{\"message\":\"wrong error\"}" + turnField + "}}"]);
        await f.Until(() => f.Store.GetJob(f.Child)!.ReasonCode == "native_interrupt_unconfirmed");
        Assert.NotEqual(JobStatus.Completed, f.Store.GetJob(f.Child)!.Status);
        Assert.NotEqual(JobStatus.Failed, f.Store.GetJob(f.Child)!.Status);
        f.Complete();
        await f.Until(() => f.Store.GetJob(f.Child)!.Status == JobStatus.Completed);
        Assert.Equal("actual result", f.Store.GetJob(f.Child)!.ResultText);
    }

    [Fact]
    public async Task Explicit_native_release_warning_survives_mcp_mapping()
    {
        using var f = await NativeInterruptFixture.Create();
        var endpoint = Endpoint(f);
        var pending = endpoint.Handle(new() { Op = IpcProtocol.JobStop, JobId = f.Child });
        Assert.True(pending.Ok);
        Assert.Contains("agent may still run", ((TextContentBlock)JobsMcpBridge.ToolResult(pending).Content[0]).Text);
        f.Quarantine("native_interrupt_unconfirmed");
        var response = endpoint.Handle(new() { Op = IpcProtocol.JobRelease, JobId = f.Child });
        Assert.True(response.Ok);
        Assert.Equal("native_released", response.Outcome);
        using var wire = JsonDocument.Parse(((TextContentBlock)JobsMcpBridge.ToolResult(response).Content[0]).Text);
        Assert.Contains("agent may still run", wire.RootElement.GetProperty("error_detail").GetString());
        Assert.Null(f.Jobs.NewStore().PendingNativeInterrupt(f.Child));
        Assert.Equal(0, f.Control.Escapes);
    }

    static JobsEndpoint Endpoint(NativeInterruptFixture f) => new(f.Accept, f.Jobs.Get(), f.Follow, f.Jobs.List(), f.Stop,
        DurabilityCheckpoints.None, () => { });
}
