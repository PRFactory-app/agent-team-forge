using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class NativeInterruptRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_stop_while_working_interrupts_and_retains_pane(bool codex)
    {
        using var f = await NativeInterruptFixture.Create(codex);
        await AssertConfirmedStop(f, false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_interrupt_job_on_native_child_requires_confirmation(bool codex)
    {
        using var f = await NativeInterruptFixture.Create(codex);
        await AssertConfirmedStop(f, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Api_failure_retains_pane_then_followup_stop_interrupts_new_turn(bool codex)
    {
        using var f = await NativeInterruptFixture.Create(codex, failedParent: true);
        Assert.Equal((JobStatus.Failed, "agent_api_error"), (f.Store.GetJob(f.Parent)!.Status, f.Store.GetJob(f.Parent)!.ReasonCode));
        await AssertConfirmedStop(f, false);
    }

    static async Task AssertConfirmedStop(NativeInterruptFixture f, bool interrupt)
    {
        var stopped = f.Stop.Execute(f.Child, interrupt);
        Assert.Equal("native_interrupt_pending", stopped.Outcome);
        Assert.Equal(JobStatus.Running, f.Store.GetJob(f.Child)!.Status);
        Assert.True(f.Store.IsSessionFenced(f.Child));
        await f.Until(() => f.Control.Escapes == 1);
        Assert.NotEqual(JobStatus.Cancelled, f.Store.GetJob(f.Child)!.Status);
        f.Confirm();
        await f.Until(() => f.Store.GetJob(f.Child)!.Status == JobStatus.Cancelled);
        Assert.Equal(interrupt ? "interrupted" : "stopped", f.Store.GetJob(f.Child)!.ReasonCode);
        Assert.False(f.Store.IsSessionFenced(f.Child));
        Assert.Equal(1, f.Control.Starts);
        Assert.Equal(1, f.Control.Escapes);
        Assert.Equal(0, f.Control.Closes);
        Assert.True(f.Backend.HasIdleClaudeSession(f.Session) || f.Backend.HasIdleCodexSession(f.Session));
    }
}

internal sealed class NativeCloseBackend : IJobBackend, IInteractiveSessionStop
{
    public bool AllowClose { get; set; }
    public int CloseCalls { get; private set; }
    public string? Session { get; private set; }
    public IBackendRun Start(BackendRequest request) => throw new InvalidOperationException("No CLI in close tests.");
    public bool HasIdleSession(string sessionId) => false;
    public bool StopIdleSession(string sessionId)
    {
        Session = sessionId;
        CloseCalls++;
        return AllowClose;
    }
    public void StopAllIdleSessions() { }
}

internal sealed class NativeInterruptFixture : IDisposable
{
    readonly JobFixture jobs = new();
    readonly TempStateDir state = new();
    public JobStore Store => jobs.Store;
    public JobFixture Jobs => jobs;
    public BackendCatalog Catalog { get; private set; } = null!;
    public bool Codex => codex;
    public InterruptControl Control { get; } = new();
    public HerdrInteractiveBackend Backend { get; private set; } = null!;
    public DispatchJob Dispatcher { get; private set; } = null!;
    public StopJob Stop { get; private set; } = null!;
    public FollowUpJob Follow { get; private set; } = null!;
    public AcceptJob Accept { get; private set; } = null!;
    public string Parent { get; private set; } = "";
    public string Child { get; private set; } = "";
    public string Correlation { get; private set; } = "";
    public string Session => codex ? "codex-interrupt" : "claude-interrupt";
    public string Home => Path.Combine(state.Path, "home");
    public string Transcript { get; private set; } = "";
    bool codex;

    public static async Task<NativeInterruptFixture> Create(bool codex = false, bool failedParent = false, bool claimChild = true,
        Func<string, string, string, CancellationToken, Task<CodexSubmission>>? submit = null)
    {
        var f = new NativeInterruptFixture { codex = codex };
        try
        {
            var dir = Directory.CreateDirectory(Path.Combine(f.Home, codex ? "sessions" : "projects"));
            f.Transcript = Path.Combine(dir.FullName, codex ? "rollout-test-" + f.Session + ".jsonl" : f.Session + ".jsonl");
            f.Backend = new HerdrInteractiveBackend(f.Control, new ParentReader(f.Session, f.Transcript, failedParent),
                codex ? InteractiveAgentKind.Codex : InteractiveAgentKind.Claude, f.state.Path)
            { LiveSessionProbe = (_, _) => new HashSet<string> { f.Session } };
            var name = codex ? BackendCatalog.Codex : BackendCatalog.Claude;
            var catalog = new BackendCatalog().Register(name, () => f.Backend);
            f.Catalog = catalog;
            f.Accept = new(f.Store, JobFixture.Operator, f.jobs.Limits, true, f.jobs.Admission, catalog.Names);
            f.Parent = f.Accept.Execute(new SubmitJobRequest("parent", "work", null, false) { Backend = name }).Job!.JobId;
            f.Dispatcher = new(f.Store, catalog, f.jobs.Limits, DurabilityCheckpoints.None, f.jobs.Admission, _ => { })
            { SubmitNativeCodex = submit ?? ((_, _, _, _) => throw new InvalidOperationException("No real CLI in tests.")) };
            await f.Dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
            f.Follow = new(f.Store, JobFixture.Operator, f.Accept);
            f.Child = f.Follow.Execute(new FollowUpRequest(f.Parent, "next", "next")).Job!.JobId;
            if (claimChild)
            {
                Assert.True(f.Backend.TakeIdleForNativeTurn(f.Session));
                var claim = codex ? f.Store.BeginNativeCodexAttempt(_ => true, f.Home)!
                    : f.Store.BeginNativeClaudeAttempt(f.Parent, f.Home, _ => true)!;
                f.Correlation = claim.Correlation;
                f.WriteInput();
            }
            f.Stop = new(f.Store, JobFixture.Operator, f.Dispatcher.CancelRunning, f.Dispatcher.CloseUnclaimedFollowUp,
                f.Dispatcher.StopReconciled, f.Dispatcher.ForgetReconciledOwnership, f.Dispatcher.InterruptRunning, f.Dispatcher.ReleaseNative);
            return f;
        }
        catch { f.Dispose(); throw; }
    }

    public void WriteInput(bool started = true)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        File.WriteAllLines(Transcript, codex
            ? [$$$"""{"type":"session_meta","payload":{"id":"{{{Session}}}","source":"cli"}}""",
                $$$"""{"type":"response_item","timestamp":"{{{timestamp}}}","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"atf-corr:{{{Correlation}}}"}],"internal_chat_message_metadata_passthrough":{"turn_id":"target-turn"} } }""",
                started ? """{"type":"event_msg","payload":{"type":"task_started","turn_id":"target-turn"}}""" : "{}"]
            : [$$$"""{"type":"user","isSidechain":false,"sessionId":"{{{Session}}}","timestamp":"{{{timestamp}}}","message":{"role":"user","content":"atf-corr:{{{Correlation}}}"}}"""]);
    }

    public void Confirm(string? timestamp = null, string turn = "target-turn") => File.AppendAllLines(Transcript,
        [codex
            ? $$$"""{"type":"event_msg","timestamp":"{{{timestamp ?? DateTimeOffset.UtcNow.ToString("O")}}}","payload":{"type":"turn_aborted","reason":"interrupted","turn_id":"{{{turn}}}"}}"""
            : $$$"""{"type":"user","isSidechain":false,"sessionId":"{{{Session}}}","timestamp":"{{{timestamp ?? DateTimeOffset.UtcNow.ToString("O")}}}","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user for tool use]"}]}}"""]);

    public void Complete() => File.AppendAllLines(Transcript,
        [codex ? """{"type":"event_msg","payload":{"type":"task_complete","turn_id":"target-turn","last_agent_message":"actual result"}}"""
            : $$$"""{"type":"assistant","sessionId":"{{{Session}}}","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"actual result"}]}}"""]);

    public async Task Until(Func<bool> condition)
    {
        await Bounded.Until(() =>
        {
            Reconcile();
            return condition();
        }, "native interrupt observation");
    }

    public void Reconcile()
    {
        if (codex) { Dispatcher.ReconcileNativeCodex(); } else { Dispatcher.ReconcileNativeClaude(); }
    }

    public void Quarantine(string reason)
    {
        var run = Store.GetRuns(Child)[^1];
        Assert.True(Store.EndUnsuccessfully(new(Child, run.RunId, run.Generation, run.Correlation), JobStatus.NeedsReconciliation, reason));
    }

    public void HumanInput() => File.AppendAllLines(Transcript,
        [codex ? """{"type":"event_msg","payload":{"type":"user_message","message":"new input"}}"""
            : $$$"""{"type":"user","sessionId":"{{{Session}}}","message":{"role":"user","content":"new input"}}"""]);

    public void Dispose()
    {
        Dispatcher?.Dispose();
        Backend?.Dispose();
        jobs.Dispose();
        state.Dispose();
    }

    sealed class ParentReader(string session, string file, bool failed) : IInteractiveTranscriptReader
    {
        public InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started)
        {
            launch.NativeTranscript = new(session, file);
            if (!failed) { return new(session, "ready", Completed: true); }
            if (!File.Exists(file))
            {
                File.WriteAllLines(file, launch.Kind == InteractiveAgentKind.Codex
                    ? [$$$"""{"type":"session_meta","payload":{"id":"{{{session}}}","source":"cli"}}""",
                        $$$"""{"type":"event_msg","payload":{"type":"user_message","message":"{{{correlationMarker}}}"}}""",
                        """{"type":"event_msg","payload":{"type":"task_complete","error":{"message":"Selected model is at capacity","codex_error_info":"server_overloaded"}}}"""]
                    : [$$$"""{"type":"user","isSidechain":false,"sessionId":"{{{session}}}","message":{"role":"user","content":"{{{correlationMarker}}}"}}""",
                        $$$"""{"type":"assistant","sessionId":"{{{session}}}","isApiErrorMessage":true,"message":{"role":"assistant","stop_reason":"stop_sequence","content":[{"type":"text","text":"API Error: Connection lost mid-response. The response above may be incomplete."}]}}""",
                        $$$"""{"type":"system","sessionId":"{{{session}}}","subtype":"turn_duration","durationMs":1000}"""]);
            }
            return new InteractiveTranscriptReader(_ => null).Read(launch, correlationMarker, started);
        }
        public string? FindPiSessionDirectory(string root, string sessionId) => null;
    }

    internal sealed class InterruptControl : IHerdrAgentControl
    {
        public int Starts;
        public int Escapes;
        public int Closes;
        public Func<Task>? BeforeKey { get; set; }
        public Func<Task>? AfterValidation { get; set; }
        public Task StartAsync(InteractiveLaunch launch, CancellationToken cancellationToken) { Starts++; return Task.CompletedTask; }
        public Task PromptAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task InterruptAsync(InteractiveLaunch launch, CancellationToken cancellationToken) => throw new InvalidOperationException("Native turns must use verified key dispatch.");
        public async Task<bool> SendInterruptKeyAsync(InteractiveLaunch launch, Func<bool> targetCurrent, CancellationToken cancellationToken)
        {
            if (BeforeKey is not null) { await BeforeKey(); }
            if (!targetCurrent()) { return false; }
            if (AfterValidation is not null) { await AfterValidation(); }
            Interlocked.Increment(ref Escapes);
            return true;
        }
        public Task<InteractiveAgentStatus> StatusAsync(InteractiveLaunch launch, CancellationToken cancellationToken) => Task.FromResult(InteractiveAgentStatus.Idle);
        public ProcessIdentity? PaneShell(InteractiveLaunch launch) => new(Environment.ProcessId, 1);
        public void StopOwned(InteractiveLaunch launch) { Closes++; }
        public bool PaneIsGone(InteractiveLaunch launch) => false;
    }
}
