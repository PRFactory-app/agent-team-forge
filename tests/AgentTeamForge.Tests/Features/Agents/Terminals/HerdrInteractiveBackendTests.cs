using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Recovery;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Terminals;

public sealed class HerdrInteractiveBackendTests
{
    [Fact]
    public async Task StartsRealTuiSurfaceAndEmitsNativeSessionAndResult()
    {
        var control = new FakeControl();
        var reader = new FakeReader(new InteractiveTranscript("native-1", "finished"));
        var backend = new HerdrInteractiveBackend(control, reader, InteractiveAgentKind.Codex, Path.GetTempPath());
        await using var run = backend.Start(new BackendRequest("job-1", "corr-1", "do work", "") { WorkingDirectory = Path.GetTempPath() });

        Assert.Equal(InteractiveAgentKind.Codex, control.Launch?.Kind);
        Assert.Null(run.ProcessId);
        await run.DeliverAsync(CancellationToken.None);
        var evidence = await Collect(run);

        Assert.Contains("atf-corr:corr-1", control.Prompt);
        Assert.Collection(evidence,
            e => Assert.Equal(new BackendEvidence.Ack("corr-1"), e),
            e => Assert.Equal(new BackendEvidence.Session("corr-1", "native-1"), e),
            e => Assert.Equal(new BackendEvidence.Result("corr-1", "finished"), e),
            e => Assert.IsType<BackendEvidence.EndOfOutput>(e));
    }

    [Fact]
    public async Task StreamsNewTranscriptMessagesToJobLogWhileWorking()
    {
        var root = Path.Combine(Path.GetTempPath(), "atf-herdr-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var logs = new JobLogs(root);
            var control = new FakeControl { Statuses = new Queue<InteractiveAgentStatus>([InteractiveAgentStatus.Working, InteractiveAgentStatus.Done]) };
            var reader = new SequenceReader(
                new InteractiveTranscript("native-1", "planning", ["planning"]),
                new InteractiveTranscript("native-1", "finished", ["planning", "finished"]));
            var backend = new HerdrInteractiveBackend(control, reader, InteractiveAgentKind.Codex, root);
            await using var run = backend.Start(new BackendRequest("job-1", "corr-1", "do work", "")
            {
                WorkingDirectory = root,
                Output = logs.BeginRun("job-1", "run-1", "codex"),
            });
            await run.DeliverAsync(CancellationToken.None);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await using var evidence = run.ReadEvidenceAsync(deadline.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            Assert.True(await evidence.MoveNextAsync()); // Ack
            Assert.True(await evidence.MoveNextAsync()); // Session from the working poll
            Assert.Contains("planning\n", logs.Read("job-1").Text);
            Assert.DoesNotContain("finished\n", logs.Read("job-1").Text);

            while (await evidence.MoveNextAsync()) { }
            var text = logs.Read("job-1").Text;
            Assert.Equal(1, text.Split("planning\n", StringSplitOptions.None).Length - 1);
            Assert.Equal(1, text.Split("finished\n", StringSplitOptions.None).Length - 1);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FollowUpUsesNativeResumeArgumentsAndSameSession()
    {
        var control = new FakeControl();
        var reader = new FakeReader(new InteractiveTranscript("native-1", "follow-up finished"));
        var backend = new HerdrInteractiveBackend(control, reader, InteractiveAgentKind.Claude, Path.GetTempPath());
        await using var run = backend.Start(new BackendRequest("job-2", "corr-2", "continue", "")
        {
            WorkingDirectory = Path.GetTempPath(),
            ResumeSessionId = "native-1",
        });
        await run.DeliverAsync(CancellationToken.None);
        var evidence = await Collect(run);

        Assert.Equal("native-1", control.Launch?.ResumeSessionId);
        Assert.Equal(["--permission-mode", "bypassPermissions", "--resume", "native-1"], HerdrAgentControl.AgentArguments(control.Launch!));
        Assert.Contains(evidence, e => e == new BackendEvidence.Session("corr-2", "native-1"));
        Assert.Contains(evidence, e => e == new BackendEvidence.Result("corr-2", "follow-up finished"));
    }

    [Fact]
    public async Task Interrupt_keeps_the_live_tab_and_prompts_it_again()
    {
        var control = new FakeControl { Status = InteractiveAgentStatus.Working };
        var backend = new HerdrInteractiveBackend(control,
            new FakeReader(new InteractiveTranscript("native-1", null)), InteractiveAgentKind.Codex, Path.GetTempPath());
        var first = backend.Start(new BackendRequest("parent", "corr-parent", "first", "") { WorkingDirectory = Path.GetTempPath() });
        await first.DeliverAsync(CancellationToken.None);
        await using (var evidence = first.ReadEvidenceAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.True(await evidence.MoveNextAsync()); // ack
            Assert.True(await evidence.MoveNextAsync()); // native session
            Assert.Equal(new BackendEvidence.Session("corr-parent", "native-1"), evidence.Current);
        }

        first.InterruptTurn();
        await first.DisposeAsync();
        Assert.Equal(1, control.Interrupts);
        Assert.False(control.Stopped);

        var original = control.Launch;
        await using var second = backend.Start(new BackendRequest("child", "corr-child", "second", "")
        { WorkingDirectory = Path.GetTempPath(), ResumeSessionId = "native-1" });
        await second.DeliverAsync(CancellationToken.None);
        Assert.Equal(1, control.Starts);
        Assert.Same(original, control.Launch);
        Assert.Contains("second", control.Prompt);
    }

    [Fact]
    public async Task ChangedFollowUpSelectionResumesInANewTab()
    {
        var control = new FakeControl { Status = InteractiveAgentStatus.Working };
        var backend = new HerdrInteractiveBackend(control,
            new FakeReader(new InteractiveTranscript("native-1", null)), InteractiveAgentKind.Codex, Path.GetTempPath());
        var first = backend.Start(new BackendRequest("parent", "corr-parent", "first", "model=gpt-6-luna;effort=high")
        { WorkingDirectory = Path.GetTempPath() });
        await first.DeliverAsync(CancellationToken.None);
        await using (var evidence = first.ReadEvidenceAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.True(await evidence.MoveNextAsync());
            Assert.True(await evidence.MoveNextAsync());
        }
        first.InterruptTurn();
        await first.DisposeAsync();

        await using var second = backend.Start(new BackendRequest("child", "corr-child", "second", "model=gpt-6-sol;effort=medium")
        { WorkingDirectory = Path.GetTempPath(), ResumeSessionId = "native-1" });
        Assert.True(control.Stopped);
        Assert.Equal(2, control.Starts);
        Assert.Equal("gpt-6-sol", control.Launch?.Model);
        Assert.Equal("medium", control.Launch?.Effort);
        Assert.Equal("native-1", control.Launch?.ResumeSessionId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Unclaimed_interrupted_follow_up_closes_its_tab_on_stop_or_ttl(bool expire, bool stopBeforeInterrupt)
    {
        using var f = new JobFixture();
        var control = new FakeControl { Status = InteractiveAgentStatus.Working };
        var backend = new HerdrInteractiveBackend(control,
            new FakeReader(new InteractiveTranscript("native-1", null)), InteractiveAgentKind.Codex, Path.GetTempPath());
        var catalog = new BackendCatalog().Register(BackendCatalog.Codex, () => backend);
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, f.Admission, catalog.Names);
        var parent = accept.Execute(new SubmitJobRequest("parent", "first", null, false)
        { Backend = BackendCatalog.Codex, Cwd = Path.GetTempPath() }).Job!;
        var claim = f.Store.BeginNextAttempt()!;
        var first = backend.Start(new BackendRequest(parent.JobId, claim.Correlation, "first", "") { WorkingDirectory = Path.GetTempPath() });
        await first.DeliverAsync(CancellationToken.None);
        await using (var evidence = first.ReadEvidenceAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.True(await evidence.MoveNextAsync());
            Assert.True(await evidence.MoveNextAsync());
        }
        Assert.True(f.Store.RecordSession(new RunRef(parent.JobId, claim.RunId, claim.Generation, claim.Correlation), "native-1"));
        var child = new FollowUpJob(f.Store, JobFixture.Operator, accept, _ => { }).Execute(
            new FollowUpRequest(parent.JobId, "second", "child") { Interrupt = true, QueueTtlSeconds = expire ? 1 : null }).Job!;
        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        if (stopBeforeInterrupt)
        {
            new StopJob(f.Store, JobFixture.Operator, dispatcher.CancelRunning, dispatcher.CloseUnclaimedFollowUp).Execute(child.JobId);
        }
        first.InterruptTurn();
        await first.DisposeAsync();
        if (stopBeforeInterrupt)
        {
            dispatcher.CloseInterruptedIfUnclaimed(parent.JobId);
        }
        if (expire)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1100), TestContext.Current.CancellationToken);
            dispatcher.SweepExpiredQueued();
        }
        else if (!stopBeforeInterrupt)
        {
            new StopJob(f.Store, JobFixture.Operator, dispatcher.CancelRunning, dispatcher.CloseUnclaimedFollowUp).Execute(child.JobId);
        }

        Assert.Equal(JobStatus.Cancelled, f.Store.GetJob(child.JobId)!.Status);
        Assert.True(control.Stopped);
        await using var resumed = backend.Start(new BackendRequest("later", "corr-later", "later", "")
        { WorkingDirectory = Path.GetTempPath(), ResumeSessionId = "native-1" });
        Assert.Equal(2, control.Starts); // The cancelled child cannot hand off the old pane.
    }

    [Fact]
    public async Task Stopping_one_queued_follow_up_keeps_the_tab_for_a_queued_sibling()
    {
        using var f = new JobFixture();
        var control = new FakeControl { Status = InteractiveAgentStatus.Working };
        var backend = new HerdrInteractiveBackend(control,
            new FakeReader(new InteractiveTranscript("native-1", null)), InteractiveAgentKind.Codex, Path.GetTempPath());
        var catalog = new BackendCatalog().Register(BackendCatalog.Codex, () => backend);
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, f.Admission, catalog.Names);
        var parent = accept.Execute(new SubmitJobRequest("parent", "first", null, false)
        { Backend = BackendCatalog.Codex, Cwd = Path.GetTempPath() }).Job!;
        var claim = f.Store.BeginNextAttempt()!;
        var first = backend.Start(new BackendRequest(parent.JobId, claim.Correlation, "first", "") { WorkingDirectory = Path.GetTempPath() });
        await first.DeliverAsync(CancellationToken.None);
        await using (var evidence = first.ReadEvidenceAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.True(await evidence.MoveNextAsync());
            Assert.True(await evidence.MoveNextAsync());
        }
        Assert.True(f.Store.RecordSession(new RunRef(parent.JobId, claim.RunId, claim.Generation, claim.Correlation), "native-1"));
        var followUp = new FollowUpJob(f.Store, JobFixture.Operator, accept, _ => { });
        var child = followUp.Execute(new FollowUpRequest(parent.JobId, "second", "child") { Interrupt = true }).Job!;
        first.InterruptTurn();
        await first.DisposeAsync();
        var sibling = followUp.Execute(new FollowUpRequest(parent.JobId, "third", "sibling")).Job!;
        Assert.Equal(JobStatus.Queued, f.Store.GetJob(sibling.JobId)!.Status);
        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });

        new StopJob(f.Store, JobFixture.Operator, dispatcher.CancelRunning, dispatcher.CloseUnclaimedFollowUp).Execute(child.JobId);

        Assert.False(control.Stopped);
        await using var resumed = backend.Start(new BackendRequest(sibling.JobId, "corr-sibling", "third", "")
        { WorkingDirectory = Path.GetTempPath(), ResumeSessionId = "native-1" });
        Assert.Equal(1, control.Starts);
    }

    [Fact]
    public void Restart_logs_and_keeps_unprovable_Herdr_record_without_blocking_startup()
    {
        using var f = new JobFixture();
        using var state = new TempStateDir();
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null,
            Path.Combine(state.Path, "herdr", "atftest.bootstrap"));
        Directory.CreateDirectory(Path.GetDirectoryName(launch.BootstrapPath)!);
        HerdrOwnedSessions.Save(launch, new OwnedHerdrSession("atf-test", "/tmp/atf-test.sock", 123, 456, "owner", "workspace"));
        var logs = new List<string>();

        var recovered = new RecoverOnStartup(f.Store, () => HerdrOwnedSessions.Recover(state.Path,
            _ => throw new HerdrLaunchException("teardown refused: running server is not the recorded process"), logs.Add)).Execute();

        Assert.Empty(recovered);
        Assert.Contains(logs, l => l.Contains("teardown refused"));
        Assert.True(File.Exists(HerdrOwnedSessions.PathFor(launch))); // Retried on the next start.
    }

    [Fact]
    public void Restart_closes_recorded_Herdr_session_before_claiming_interrupted_follow_up()
    {
        using var f = new JobFixture();
        using var state = new TempStateDir();
        var parent = f.Submit("parent");
        var claim = f.Store.BeginNextAttempt()!;
        Assert.True(f.Store.RecordSession(new RunRef(parent.JobId, claim.RunId, claim.Generation, claim.Correlation), "native-1"));
        var child = f.Store.AcceptOrGet(new NewJob(JobFixture.Operator.Principal, JobFixture.Operator.Team,
            JobFixture.Operator.Agent, FollowUpJob.Operation, "child", "fingerprint", "second", "")
        { ParentJobId = parent.JobId, InterruptParent = true }, f.Limits.QueueLimit).Job!;
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null,
            Path.Combine(state.Path, "herdr", "atftest.bootstrap"));
        Directory.CreateDirectory(Path.GetDirectoryName(launch.BootstrapPath)!);
        var owned = new OwnedHerdrSession("atf-test", "/tmp/atf-test.sock", 123, 456, "owner", "workspace");
        HerdrOwnedSessions.Save(launch, owned);
        var stopped = false;

        var recovered = new RecoverOnStartup(f.Store, () => HerdrOwnedSessions.Recover(state.Path, session =>
        {
            Assert.Equal(owned, session);
            Assert.Equal(JobStatus.Queued, f.Store.GetJob(child.JobId)!.Status);
            stopped = true;
            return Task.CompletedTask;
        }, _ => { })).Execute();

        Assert.True(stopped);
        Assert.Empty(recovered);
        Assert.False(File.Exists(HerdrOwnedSessions.PathFor(launch)));
        Assert.Equal(child.JobId, f.Store.BeginNextAttempt()!.Job.JobId);
    }

    [Fact]
    public async Task Settled_turn_retains_owned_tab_until_follow_up_or_stop_agent()
    {
        var control = new FakeControl { Status = InteractiveAgentStatus.Done };
        var backend = new HerdrInteractiveBackend(control,
            new FakeReader(new InteractiveTranscript("native-1", "finished")), InteractiveAgentKind.Claude, Path.GetTempPath());
        var first = backend.Start(new BackendRequest("parent", "corr-parent", "first", "") { WorkingDirectory = Path.GetTempPath() });
        await first.DeliverAsync(CancellationToken.None);
        await Collect(first);
        await first.DisposeAsync();
        Assert.False(control.Stopped);

        first.InterruptTurn();
        Assert.Equal(0, control.Interrupts);

        await using var second = backend.Start(new BackendRequest("child", "corr-child", "second", "")
        { WorkingDirectory = Path.GetTempPath(), ResumeSessionId = "native-1" });
        Assert.Equal(1, control.Starts);
        Assert.Null(control.Launch!.ResumeSessionId);
        await second.DisposeAsync();
        Assert.True(backend.StopIdleSession("native-1"));
        Assert.True(control.Stopped);
        Assert.False(backend.StopIdleSession("native-1"));
    }

    [Fact]
    public void PiResumeUsesContinueInTheLocatedSessionDirectory()
    {
        var launch = new InteractiveLaunch(InteractiveAgentKind.Pi, "atftest", "/tmp", "native-pi", "/tmp/pi-one", "/tmp/bootstrap");
        var args = HerdrAgentControl.AgentArguments(launch);
        Assert.Contains("--continue", args);
        Assert.DoesNotContain("--session-id", args);
        Assert.Contains("/tmp/pi-one", args);
    }

    [Theory]
    [InlineData(InteractiveAgentKind.Claude, "model=opus;effort=high", "--model", "opus", "--effort", "high")]
    [InlineData(InteractiveAgentKind.Codex, "model=gpt-6-sol;effort=xhigh", "-m", "gpt-6-sol", "-c", "model_reasoning_effort=\"xhigh\"")]
    [InlineData(InteractiveAgentKind.Pi, "model=gpt-6-luna;effort=max", "--model", "openai-codex/gpt-6-luna", "--thinking", "max")]
    public void LaunchAndResumeCarryResolvedSelection(InteractiveAgentKind kind, string options,
        string modelFlag, string model, string effortFlag, string effort)
    {
        var launch = new InteractiveLaunch(kind, "atftest", "/tmp", "native-1", "/tmp/pi-one", "/tmp/bootstrap")
            .WithSelection(options);
        var args = HerdrAgentControl.AgentArguments(launch);
        Assert.Equal([modelFlag, model], args.SkipWhile(arg => arg != modelFlag).Take(2));
        Assert.Equal([effortFlag, effort], args.SkipWhile(arg => arg != effortFlag).Take(2));
        Assert.Contains(kind switch { InteractiveAgentKind.Claude => "--resume", InteractiveAgentKind.Codex => "resume", _ => "--continue" }, args);
    }

    [Fact]
    public void MissingDesktopNeverFallsBackToHeadless()
    {
        var control = new FakeControl { Unavailable = true };
        var backend = new HerdrInteractiveBackend(control, new FakeReader(null), InteractiveAgentKind.Codex, Path.GetTempPath());
        Assert.Throws<InvalidOperationException>(() => backend.Start(new BackendRequest("job", "corr", "text", "") { WorkingDirectory = Path.GetTempPath() }));
        Assert.False(control.Prompted);
    }

    [Fact]
    public void PiTranscriptRequiresTheTurnMarkerAndReturnsNativeSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "atf-transcript-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "pi-sessions", "one");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllLines(Path.Combine(dir, "session.jsonl"),
            [
                """{"type":"session","id":"pi-native"}""",
                """{"type":"message","message":{"role":"assistant","content":[{"type":"text","text":"old answer"}]}}""",
                """{"type":"message","message":{"role":"user","content":[{"type":"text","text":"atf-corr:new-turn"}]}}""",
                """{"type":"message","message":{"role":"assistant","content":[{"type":"text","text":"new answer"}]}}""",
                """{"type":"last-prompt","lastPrompt":"atf-corr:new-turn"}""",
            ]);
            var reader = new InteractiveTranscriptReader();
            var launch = new InteractiveLaunch(InteractiveAgentKind.Pi, "atftest", root, null, dir, Path.Combine(root, "bootstrap"));
            Assert.Equal(dir, reader.FindPiSessionDirectory(Path.Combine(root, "pi-sessions"), "pi-native"));
            var transcript = reader.Read(launch, "atf-corr:new-turn", DateTimeOffset.UtcNow);
            Assert.Equal("pi-native", transcript?.SessionId);
            Assert.Equal("new answer", transcript?.Message);
            Assert.Equal(["new answer"], transcript?.Progress);
            Assert.Null(reader.Read(launch, "atf-corr:other-turn", DateTimeOffset.UtcNow));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task HerdrControlFaultsBecomeEvidenceNotDispatcherFaults()
    {
        var failedPrompt = new HerdrInteractiveBackend(new FakeControl { FailPrompt = true }, new FakeReader(null), InteractiveAgentKind.Codex, Path.GetTempPath());
        await using (var run = failedPrompt.Start(new BackendRequest("job", "corr", "text", "") { WorkingDirectory = Path.GetTempPath() }))
        {
            await run.DeliverAsync(CancellationToken.None);
            Assert.Equal([new BackendEvidence.ProtocolError("interactive_delivery_not_confirmed")], await Collect(run));
        }

        var failedStatus = new HerdrInteractiveBackend(new FakeControl { FailStatus = true }, new FakeReader(null), InteractiveAgentKind.Codex, Path.GetTempPath());
        await using (var run = failedStatus.Start(new BackendRequest("job", "corr", "text", "") { WorkingDirectory = Path.GetTempPath() }))
        {
            await run.DeliverAsync(CancellationToken.None);
            Assert.Contains(new BackendEvidence.ProtocolError("interactive_control_failed"), await Collect(run));
        }
    }

    [Fact]
    public async Task ExitedAgentClosesItsOwnedSession()
    {
        var control = new FakeControl { Status = InteractiveAgentStatus.Gone };
        var backend = new HerdrInteractiveBackend(control, new FakeReader(null), InteractiveAgentKind.Claude, Path.GetTempPath());
        var run = backend.Start(new BackendRequest("job", "corr", "text", "") { WorkingDirectory = Path.GetTempPath() });
        await run.DeliverAsync(CancellationToken.None);
        Assert.Contains(new BackendEvidence.ProtocolError("interactive_agent_exited"), await Collect(run));
        await run.DisposeAsync();
        Assert.True(control.Stopped);
    }

    [Theory]
    [InlineData("herdr --session atf-x exited 1: timeout", true)]
    [InlineData("herdr --session atf-x exited 1: agent_prompt_stalled", true)]
    [InlineData("herdr --session atf-x exited 1: agent_blocked", false)]
    public void StalledOrTimedOutPromptIsObservedNotFatal(string message, bool unsettled) =>
        Assert.Equal(unsettled, HerdrAgentControl.IsUnsettledPrompt(new HerdrLaunchException(message)));

    /// <summary>Opt-in live agent run; HerdrTerminal creates only an atf-test-* session.</summary>
    [Fact]
    public async Task RealHerdr_InteractiveCodexInOwnedTestSession()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ATF_REAL_HERDR") == "1", "set ATF_REAL_HERDR=1 for a live interactive Codex run");
        var root = Path.Combine(Path.GetTempPath(), "atf-herdr-backend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var seed = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value, StringComparer.Ordinal);
        var terminal = new HerdrTerminal(new HerdrTerminalOptions { Environment = seed, SessionPrefix = "atf-test-" });
        var trace = new TracingControl(new HerdrAgentControl(terminal));
        var backend = new HerdrInteractiveBackend(trace, new InteractiveTranscriptReader(), InteractiveAgentKind.Codex, root);
        IBackendRun? run = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            run = backend.Start(new BackendRequest("real-test", Guid.NewGuid().ToString("N"), "Reply exactly ATF_OK.", "") { WorkingDirectory = root });
            await run.DeliverAsync(deadline.Token);
            var evidence = new List<BackendEvidence>();
            try
            {
                await foreach (var item in run.ReadEvidenceAsync(deadline.Token))
                {
                    evidence.Add(item);
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                throw new Xunit.Sdk.XunitException("interactive result timeout; trace=" + trace.Events);
            }
            Assert.Contains(evidence, item => item is BackendEvidence.Session);
            Assert.Contains(evidence, item => item is BackendEvidence.Result r && r.Output.Contains("ATF_OK", StringComparison.Ordinal));
        }
        finally
        {
            run?.TerminateOwnedChild();
            if (run is not null)
            {
                await run.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    static async Task<List<BackendEvidence>> Collect(IBackendRun run)
    {
        var result = new List<BackendEvidence>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var evidence in run.ReadEvidenceAsync(deadline.Token))
        {
            result.Add(evidence);
        }
        return result;
    }

    sealed class FakeControl : IHerdrAgentControl
    {
        public InteractiveLaunch? Launch { get; private set; }
        public int Starts { get; private set; }
        public int Interrupts { get; private set; }
        public string Prompt { get; private set; } = "";
        public bool Prompted => Prompt.Length != 0;
        public bool Unavailable { get; init; }

        public Task StartAsync(InteractiveLaunch launch, CancellationToken cancellationToken)
        {
            if (Unavailable)
            {
                throw new InteractiveTerminalUnavailableException("no desktop");
            }
            Launch = launch;
            Starts++;
            return Task.CompletedTask;
        }

        public bool FailPrompt { get; init; }
        public bool FailStatus { get; init; }
        public InteractiveAgentStatus Status { get; init; } = InteractiveAgentStatus.Done;
        public Queue<InteractiveAgentStatus>? Statuses { get; init; }
        public bool Stopped { get; private set; }

        public Task PromptAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken)
        {
            if (FailPrompt)
            {
                throw new HerdrLaunchException("herdr agent prompt exited 1: agent_blocked");
            }
            Prompt = prompt;
            return Task.CompletedTask;
        }

        public Task<InteractiveAgentStatus> StatusAsync(InteractiveLaunch launch, CancellationToken cancellationToken) =>
            FailStatus ? throw new HerdrLaunchException("herdr agent get exited 1: io") : Task.FromResult(Statuses?.Count > 0 ? Statuses.Dequeue() : Status);

        public void StopOwned(InteractiveLaunch launch) => Stopped = true;
        public Task InterruptAsync(InteractiveLaunch launch, CancellationToken cancellationToken)
        {
            Interrupts++;
            return Task.CompletedTask;
        }
    }

    sealed class FakeReader(InteractiveTranscript? output) : IInteractiveTranscriptReader
    {
        public InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started) => output;
        public string? FindPiSessionDirectory(string root, string sessionId) => "/tmp/pi-one";
    }

    sealed class SequenceReader(params InteractiveTranscript[] snapshots) : IInteractiveTranscriptReader
    {
        readonly Queue<InteractiveTranscript> _snapshots = new(snapshots);
        public InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started) =>
            _snapshots.Count > 0 ? _snapshots.Dequeue() : snapshots[^1];
        public string? FindPiSessionDirectory(string root, string sessionId) => null;
    }

    sealed class TracingControl(IHerdrAgentControl inner) : IHerdrAgentControl
    {
        readonly List<string> _events = [];
        int _polls;
        public string Events => string.Join(",", _events);

        public async Task StartAsync(InteractiveLaunch launch, CancellationToken cancellationToken)
        {
            _events.Add("start");
            await inner.StartAsync(launch, cancellationToken);
            _events.Add("started");
        }

        public async Task PromptAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken)
        {
            _events.Add("prompt");
            await inner.PromptAsync(launch, prompt, cancellationToken);
            _events.Add("prompted");
        }

        public async Task<InteractiveAgentStatus> StatusAsync(InteractiveLaunch launch, CancellationToken cancellationToken)
        {
            var status = await inner.StatusAsync(launch, cancellationToken);
            if (++_polls % 20 == 1)
            {
                _events.Add(status.ToString());
            }
            return status;
        }

        public void StopOwned(InteractiveLaunch launch) => inner.StopOwned(launch);
        public Task InterruptAsync(InteractiveLaunch launch, CancellationToken cancellationToken) => inner.InterruptAsync(launch, cancellationToken);
    }
}
