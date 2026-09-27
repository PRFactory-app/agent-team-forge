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
    public async Task Verified_exited_session_releases_fence_after_cleanup()
    {
        using var f = new JobFixture();
        using var state = new TempStateDir();
        var control = new FakeControl { Status = InteractiveAgentStatus.Gone };
        var backend = new HerdrInteractiveBackend(control, new FakeReader(null), InteractiveAgentKind.Codex, state.Path);
        var job = f.Submit("exited");
        var claim = f.Store.BeginNextAttempt()!;
        using var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        await dispatcher.RunAttemptAsync(claim, TestContext.Current.CancellationToken);
        Assert.True(control.Stopped);
        Assert.Equal(JobStatus.NeedsReconciliation, f.Store.GetJob(job.JobId)!.Status);
        Assert.False(f.Store.IsSessionFenced(job.JobId));
    }

    [Fact]
    public async Task StartsRealTuiSurfaceAndEmitsNativeSessionAndResult()
    {
        var control = new FakeControl();
        var reader = new FakeReader(new InteractiveTranscript("native-1", "finished", Completed: true));
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
                new InteractiveTranscript("native-1", "finished", ["planning", "finished"], Completed: true));
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
        var reader = new FakeReader(new InteractiveTranscript("native-1", "follow-up finished", Completed: true));
        var backend = new HerdrInteractiveBackend(control, reader, InteractiveAgentKind.Claude, Path.GetTempPath());
        await using var run = backend.Start(new BackendRequest("job-2", "corr-2", "continue", "")
        {
            WorkingDirectory = Path.GetTempPath(),
            ResumeSessionId = "native-1",
        });
        await run.DeliverAsync(CancellationToken.None);
        var evidence = await Collect(run);

        Assert.Equal("native-1", control.Launch?.ResumeSessionId);
        Assert.Equal(["--permission-mode", "bypassPermissions", "--settings", "{\"skipDangerousModePermissionPrompt\":true}", "--resume", "native-1"],
            HerdrAgentControl.AgentArguments(control.Launch!));
        Assert.Contains(evidence, e => e == new BackendEvidence.Session("corr-2", "native-1"));
        Assert.Contains(evidence, e => e == new BackendEvidence.Result("corr-2", "follow-up finished"));
    }

    [Fact]
    public async Task Interrupt_keeps_the_live_tab_and_prompts_it_again()
    {
        var control = new FakeControl { Status = InteractiveAgentStatus.Working };
        var backend = new HerdrInteractiveBackend(control,
            new FakeReader(new InteractiveTranscript("native-1", null)), InteractiveAgentKind.Codex, Path.GetTempPath());
        var parentPhases = new List<string>();
        var childPhases = new List<string>();
        var first = backend.Start(new BackendRequest("parent", "corr-parent", "first", "")
        { WorkingDirectory = Path.GetTempPath(), StartupProgress = parentPhases.Add });
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
        { WorkingDirectory = Path.GetTempPath(), ResumeSessionId = "native-1", StartupProgress = childPhases.Add });
        await second.DeliverAsync(CancellationToken.None);
        Assert.Equal(1, control.Starts);
        Assert.Same(original, control.Launch);
        Assert.Contains("second", control.Prompt);
        Assert.Equal(["ready", "submitted"], parentPhases);
        Assert.Equal(["ready", "submitted"], childPhases);
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
        f.Store.ReconcileStoppedJob(parent.JobId); // This test drives the verified interrupt without DispatchJob.
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
        f.Store.ReconcileStoppedJob(parent.JobId); // This test drives the verified interrupt without DispatchJob.
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
            _ => throw new InvalidOperationException("recovery must not invoke stop"), logs.Add)).Execute();

        Assert.Empty(recovered);
        Assert.Contains(logs, l => l.Contains("preserved owned Herdr"));
        Assert.True(File.Exists(HerdrOwnedSessions.PathFor(launch))); // Retried on the next start.
    }

    [Fact]
    public void Restart_preserves_recorded_Herdr_session_and_fences_interrupted_follow_up()
    {
        using var f = new JobFixture();
        using var state = new TempStateDir();
        var parent = f.Submit("parent");
        var claim = f.Store.BeginNextAttempt()!;
        Assert.True(f.Store.RecordSession(new RunRef(parent.JobId, claim.RunId, claim.Generation, claim.Correlation), "native-1"));
        var child = f.Store.AcceptOrGet(new NewJob(JobFixture.Operator.Principal, JobFixture.Operator.Team,
            JobFixture.Operator.Agent, FollowUpJob.Operation, "child", "fingerprint", "second", "")
        { ParentJobId = parent.JobId, InterruptParent = true }, f.Limits.QueueLimit);
        if (child is not Accepted acceptedChild)
        {
            throw new InvalidOperationException("expected acceptance");
        }
        var childJob = acceptedChild.Job;
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null,
            Path.Combine(state.Path, "herdr", "atftest.bootstrap"));
        Directory.CreateDirectory(Path.GetDirectoryName(launch.BootstrapPath)!);
        launch = launch with { JobId = parent.JobId };
        var owned = new OwnedHerdrSession("atf-test", "/tmp/atf-test.sock", 123, 456, "owner", "workspace");
        HerdrOwnedSessions.Save(launch, owned);

        var recovered = new RecoverOnStartup(f.Store, () =>
            HerdrOwnedSessions.Recover(state.Path, f.Store.FenceSession, _ => { })).Execute();

        Assert.Empty(recovered);
        Assert.True(File.Exists(HerdrOwnedSessions.PathFor(launch)));
        Assert.True(f.Store.IsSessionFenced(childJob.JobId));
        Assert.Null(f.Store.BeginNextAttempt());
        var unrelated = f.Submit("unrelated");
        Assert.Equal(unrelated.JobId, f.Store.BeginNextAttempt()!.Job.JobId);
        f.Store.ReconcileStoppedSession(parent.JobId);
        Assert.Equal(childJob.JobId, f.Store.BeginNextAttempt()!.Job.JobId);
    }

    [Fact]
    public async Task Settled_turn_retains_owned_tab_until_follow_up_or_stop_agent()
    {
        var control = new FakeControl { Status = InteractiveAgentStatus.Done };
        var backend = new HerdrInteractiveBackend(control,
            new FakeReader(new InteractiveTranscript("native-1", "finished", Completed: true)), InteractiveAgentKind.Claude, Path.GetTempPath());
        var first = backend.Start(new BackendRequest("parent", "corr-parent", "first", "") { WorkingDirectory = Path.GetTempPath() });
        await first.DeliverAsync(CancellationToken.None);
        await Collect(first);
        var binding = new NativeTranscriptBinding("native-1", "/private/native.jsonl");
        control.Launch!.NativeTranscript = binding;
        await first.DisposeAsync();
        Assert.False(control.Stopped);

        first.InterruptTurn();
        Assert.Equal(0, control.Interrupts);

        await using var second = backend.Start(new BackendRequest("child", "corr-child", "second", "")
        { WorkingDirectory = Path.GetTempPath(), ResumeSessionId = "native-1" });
        Assert.Equal(1, control.Starts);
        Assert.Null(control.Launch!.ResumeSessionId);
        Assert.Same(binding, control.Launch.NativeTranscript);
        await second.DeliverAsync(CancellationToken.None);
        Assert.Contains(new BackendEvidence.Result("corr-child", "finished"), await Collect(second));
        await second.DisposeAsync();
        Assert.True(backend.StopIdleSession("native-1"));
        Assert.True(control.Stopped);
        Assert.False(backend.StopIdleSession("native-1"));
    }

    [Fact]
    public async Task Dead_retained_tab_is_reopened_with_its_native_session()
    {
        using var state = new TempStateDir();
        var control = new FakeControl();
        var backend = new HerdrInteractiveBackend(control,
            new FakeReader(new InteractiveTranscript("native-1", "finished", Completed: true)), InteractiveAgentKind.Claude, state.Path);
        await using var first = backend.Start(new BackendRequest("parent", "first", "work", "") { WorkingDirectory = state.Path });
        await first.DeliverAsync(CancellationToken.None);
        await Collect(first);
        await first.DisposeAsync();
        Assert.True(backend.HasIdleSession("native-1"));
        control.Status = InteractiveAgentStatus.Gone;
        await using var revived = backend.Start(new BackendRequest("child", "second", "continue", "")
        { WorkingDirectory = state.Path, ResumeSessionId = "native-1" });
        Assert.Equal(2, control.Starts);
        Assert.Equal("native-1", control.Launch!.ResumeSessionId);
        Assert.False(control.Stopped);
    }

    [Fact]
    public async Task Failed_liveness_probe_keeps_retained_tab_reuse()
    {
        using var state = new TempStateDir();
        var control = new FakeControl();
        var backend = new HerdrInteractiveBackend(control,
            new FakeReader(new InteractiveTranscript("native-1", "finished", Completed: true)), InteractiveAgentKind.Claude, state.Path);
        await using var first = backend.Start(new BackendRequest("parent", "first", "work", "") { WorkingDirectory = state.Path });
        await first.DeliverAsync(CancellationToken.None);
        await Collect(first);
        await first.DisposeAsync();
        control.FailStatus = true;
        await using var second = backend.Start(new BackendRequest("child", "second", "continue", "")
        { WorkingDirectory = state.Path, ResumeSessionId = "native-1" });
        Assert.Equal(1, control.Starts);
        Assert.False(control.Stopped);
    }

    [Fact]
    public async Task Binding_error_is_uncertain_without_acknowledgement_or_result()
    {
        var control = new FakeControl();
        var backend = new HerdrInteractiveBackend(control,
            new FakeReader(new InteractiveTranscript("", null, BindingError: "interactive_binding_ambiguous")),
            InteractiveAgentKind.Codex, Path.GetTempPath());
        await using var run = backend.Start(new BackendRequest("job", "corr", "first", "") { WorkingDirectory = Path.GetTempPath() });
        await run.DeliverAsync(CancellationToken.None);
        var evidence = await Collect(run);
        Assert.Contains(new BackendEvidence.ProtocolError("interactive_binding_ambiguous"), evidence);
        Assert.DoesNotContain(evidence, item => item is BackendEvidence.Ack or BackendEvidence.Session or BackendEvidence.Result);
        Assert.Equal(1, control.Prompts);
    }

    [Fact]
    public void PiResumeUsesContinueInTheLocatedSessionDirectory()
    {
        var launch = new InteractiveLaunch(InteractiveAgentKind.Pi, "atftest", "/tmp", "native-pi", "/tmp/pi-one", "/tmp/bootstrap");
        var args = HerdrAgentControl.AgentArguments(launch);
        Assert.Contains("--continue", args);
        Assert.DoesNotContain("--session-id", args);
        Assert.Contains("/tmp/pi-one", args);
        Assert.Contains("--approve", args);
    }

    [Fact]
    public void CodexTrustsOnlyTheLaunchDirectoryViaConfigOverride()
    {
        using var state = new TempStateDir();
        var cwd = Path.Combine(state.Path, "atf-new-'\"project.v1");
        Directory.CreateDirectory(cwd);
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", cwd, null, null, "/tmp/bootstrap");

        var args = HerdrAgentControl.AgentArguments(launch);

        Assert.Equal(["-c", "projects={\"" + cwd.Replace("\"", "\\\"") + "\"={trust_level='trusted'}}"],
            args.SkipWhile(arg => arg != "-c").Take(2));
    }

    [Theory]
    [InlineData(InteractiveAgentKind.Codex, "│ model: loading\n› Ask Codex\n? for shortcuts", false)]
    [InlineData(InteractiveAgentKind.Codex, "Do you trust this directory?", false)]
    [InlineData(InteractiveAgentKind.Codex, "model: GPT-6-Luna\n› Ask Codex\n? for shortcuts", true)]
    [InlineData(InteractiveAgentKind.Codex, "Finished loading files\n› Ask Codex\n? for shortcuts", true)]
    [InlineData(InteractiveAgentKind.Claude, "Select login method:\n❯ 1. Claude account", false)]
    [InlineData(InteractiveAgentKind.Claude, "❯ Try a task\nbypass permissions on", true)]
    [InlineData(InteractiveAgentKind.Claude, "❯\u00a0Try a task\nbypass permissions on", true)]
    [InlineData(InteractiveAgentKind.Pi, "pi v0.87.1\nLoading extensions", false)]
    [InlineData(InteractiveAgentKind.Pi, "──────\n\n──────\n/tmp/work\ngpt-6-luna", true)]
    public void Readiness_requires_the_rendered_input_editor(InteractiveAgentKind kind, string screen, bool ready) =>
        Assert.Equal(ready, HerdrAgentControl.HasInputEditor(kind, screen));

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
        var effortIndex = args.ToList().IndexOf(effort);
        Assert.True(effortIndex > 0);
        Assert.Equal(effortFlag, args[effortIndex - 1]);
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
        var failedPrompt = new HerdrInteractiveBackend(new FakeControl { FailPrompt = true }, new FakeReader(null), InteractiveAgentKind.Codex, Path.GetTempPath(), startupTimeout: TimeSpan.FromMilliseconds(20));
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
    public async Task UncertainPromptCompletesWhenNativeRecordAppearsLater()
    {
        var control = new FakeControl { FailPrompt = true };
        var reader = new DelayedReader(2, new InteractiveTranscript("native", "finished", Completed: true));
        var backend = new HerdrInteractiveBackend(control, reader, InteractiveAgentKind.Codex,
            Path.GetTempPath(), startupTimeout: TimeSpan.FromSeconds(2));
        await using var run = backend.Start(new BackendRequest("job", "corr", "work", "") { WorkingDirectory = Path.GetTempPath() });
        await run.DeliverAsync(CancellationToken.None);
        var evidence = await Collect(run);
        Assert.Contains(new BackendEvidence.Ack("corr"), evidence);
        Assert.Contains(new BackendEvidence.Result("corr", "finished"), evidence);
        Assert.DoesNotContain(evidence, item => item is BackendEvidence.ProtocolError);
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
    [InlineData(InteractiveAgentKind.Claude)]
    [InlineData(InteractiveAgentKind.Codex)]
    [InlineData(InteractiveAgentKind.Pi)]
    public async Task Idle_before_late_transcript_never_resends_and_late_completion_wins(InteractiveAgentKind kind)
    {
        // Herdr reports idle long before the native transcript flushes (e.g. after a
        // timed-out/stalled prompt submission that was swallowed as uncertain).
        var control = new FakeControl { Status = InteractiveAgentStatus.Idle };
        var reader = new SequenceReader(null, null, null, null, null, null, null, null,
            new InteractiveTranscript("native-1", "finished", ["finished"], Completed: true));
        var backend = new HerdrInteractiveBackend(control, reader, kind, Path.GetTempPath(), TimeSpan.FromSeconds(10));
        await using var run = backend.Start(new BackendRequest("job", "corr", "text", "") { WorkingDirectory = Path.GetTempPath() });
        await run.DeliverAsync(CancellationToken.None);

        var evidence = await Collect(run, TimeSpan.FromSeconds(5));

        Assert.Equal(1, control.Prompts);
        Assert.Contains(new BackendEvidence.Result("corr", "finished"), evidence);
    }

    [Theory]
    [InlineData(false)] // Transcript never appears (missing or unreadable).
    [InlineData(true)]  // Interim commentary only; no native completion record.
    public async Task Idle_without_native_completion_becomes_uncertain_without_resend(bool interim)
    {
        var control = new FakeControl { Status = InteractiveAgentStatus.Idle };
        var reader = new FakeReader(interim ? new InteractiveTranscript("native-1", "let me check", ["let me check"]) : null);
        var backend = new HerdrInteractiveBackend(control, reader, InteractiveAgentKind.Codex, Path.GetTempPath(), TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(600));
        var run = backend.Start(new BackendRequest("job", "corr", "text", "") { WorkingDirectory = Path.GetTempPath() });
        await run.DeliverAsync(CancellationToken.None);

        var evidence = await Collect(run, TimeSpan.FromSeconds(5));
        await run.DisposeAsync();

        Assert.Equal(1, control.Prompts);
        Assert.DoesNotContain(evidence, e => e is BackendEvidence.Result);
        Assert.Equal(interim, evidence.Any(e => e is BackendEvidence.Ack));
        Assert.Equal(new BackendEvidence.ProtocolError(interim ? "interactive_completion_unobserved" : "interactive_delivery_not_confirmed"), evidence[^1]);
        Assert.False(control.Stopped); // Uncertain tabs are kept, with or without a native session ID.
    }

    [Fact]
    public async Task Background_task_wait_does_not_expire_as_idle_or_complete_with_interim_text()
    {
        var waiting = new InteractiveTranscript("native-1", "waiting", ["waiting"], PendingBackgroundTasks: true);
        var reader = new SequenceReader(waiting, waiting, waiting, waiting,
            new InteractiveTranscript("native-1", "DONE", ["waiting", "DONE"], Completed: true));
        var control = new FakeControl { Status = InteractiveAgentStatus.Idle };
        var backend = new HerdrInteractiveBackend(control, reader, InteractiveAgentKind.Claude, Path.GetTempPath(), TimeSpan.FromMilliseconds(100));
        await using var run = backend.Start(new BackendRequest("job", "corr", "sleep", "") { WorkingDirectory = Path.GetTempPath() });
        await run.DeliverAsync(CancellationToken.None);
        var evidence = await Collect(run, TimeSpan.FromSeconds(5));
        Assert.Contains(new BackendEvidence.Result("corr", "DONE"), evidence);
        Assert.DoesNotContain(evidence, e => e is BackendEvidence.ProtocolError);
    }

    [Fact]
    public async Task Interim_text_while_idle_waits_for_the_completion_record()
    {
        var control = new FakeControl { Status = InteractiveAgentStatus.Done };
        var reader = new SequenceReader(
            new InteractiveTranscript("native-1", "interim", ["interim"]),
            new InteractiveTranscript("native-1", "interim", ["interim"]),
            new InteractiveTranscript("native-1", "final", ["interim", "final"], Completed: true));
        var backend = new HerdrInteractiveBackend(control, reader, InteractiveAgentKind.Pi, Path.GetTempPath(), TimeSpan.FromSeconds(10));
        await using var run = backend.Start(new BackendRequest("job", "corr", "text", "") { WorkingDirectory = Path.GetTempPath() });
        await run.DeliverAsync(CancellationToken.None);

        var evidence = await Collect(run, TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(new BackendEvidence.Result("corr", "interim"), evidence);
        Assert.Contains(new BackendEvidence.Result("corr", "final"), evidence);
        Assert.Equal(1, control.Prompts);
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
        // ATF_HERDR_TEST_CWD lets the operator pre-trust an exact root in an isolated config;
        // a supplied directory that already exists is never deleted.
        var root = Environment.GetEnvironmentVariable("ATF_HERDR_TEST_CWD") is { Length: > 0 } supplied
            ? Path.GetFullPath(supplied) : Path.Combine("/tmp", "atf-herdr-turn-" + Guid.NewGuid().ToString("N"));
        var createdRoot = !Directory.Exists(root);
        Directory.CreateDirectory(root);
        var seed = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value, StringComparer.Ordinal);
        var terminal = new HerdrTerminal(new HerdrTerminalOptions { Environment = seed, SessionPrefix = "atf-test-" });
        var trace = new TracingControl(new HerdrAgentControl(terminal));
        // The reader resolves CODEX_HOME from the same seed the pane is launched with.
        var backend = new HerdrInteractiveBackend(trace, new InteractiveTranscriptReader(terminal.Env), InteractiveAgentKind.Codex, root);
        var correlation = Guid.NewGuid().ToString("N");
        IBackendRun? run = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            run = backend.Start(new BackendRequest("real-test", correlation, "Reply exactly ATF_OK.", "model=gpt-6-luna;effort=high") { WorkingDirectory = root });
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
            var result = Assert.Single(evidence, item => item is BackendEvidence.Result);
            Assert.Contains("ATF_OK", ((BackendEvidence.Result)result).Output, StringComparison.Ordinal);
            Assert.Single(evidence, item => item is BackendEvidence.EndOfOutput);
            Assert.IsType<BackendEvidence.EndOfOutput>(evidence[^1]);
            Assert.Equal(1, trace.Prompts);

            // The native transcript must hold exactly one correlated user input (no resend).
            var codexHome = terminal.Env("CODEX_HOME") is { Length: > 0 } configured ? configured : Path.Combine(terminal.Env("HOME")!, ".codex");
            var marker = "atf-corr:" + correlation;
            var transcript = Assert.Single(Directory.EnumerateFiles(Path.Combine(codexHome, "sessions"), "rollout-*.jsonl", SearchOption.AllDirectories),
                path => File.GetLastWriteTimeUtc(path) >= DateTime.UtcNow.AddMinutes(-5) && File.ReadAllText(path).Contains(marker, StringComparison.Ordinal));
            Assert.Equal(1, File.ReadLines(transcript).Count(line => line.Contains(marker, StringComparison.Ordinal)
                && line.Contains("\"type\":\"response_item\"", StringComparison.Ordinal) && line.Contains("\"role\":\"user\"", StringComparison.Ordinal)));
        }
        finally
        {
            run?.TerminateOwnedChild();
            if (run is not null)
            {
                await run.DisposeAsync();
            }
            if (createdRoot)
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    static async Task<List<BackendEvidence>> Collect(IBackendRun run, TimeSpan? timeout = null)
    {
        var result = new List<BackendEvidence>();
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
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
        public int Prompts { get; private set; }
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
        public bool FailStatus { get; set; }
        public InteractiveAgentStatus Status { get; set; } = InteractiveAgentStatus.Done;
        public Queue<InteractiveAgentStatus>? Statuses { get; init; }
        public bool Stopped { get; private set; }

        public Task PromptAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken)
        {
            if (FailPrompt)
            {
                throw new HerdrLaunchException("herdr agent prompt exited 1: agent_blocked");
            }
            launch.StartupProgress?.Invoke("ready");
            launch.StartupProgress?.Invoke("submitted");
            Prompt = prompt;
            Prompts++;
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

    sealed class DelayedReader(int emptyReads, InteractiveTranscript result) : IInteractiveTranscriptReader
    {
        int _reads;
        public InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started) =>
            Interlocked.Increment(ref _reads) <= emptyReads ? null : result;
        public string? FindPiSessionDirectory(string root, string sessionId) => null;
    }

    sealed class FakeReader(InteractiveTranscript? output) : IInteractiveTranscriptReader
    {
        public InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started) => output;
        public string? FindPiSessionDirectory(string root, string sessionId) => "/tmp/pi-one";
    }

    sealed class SequenceReader(params InteractiveTranscript?[] snapshots) : IInteractiveTranscriptReader
    {
        readonly Queue<InteractiveTranscript?> _snapshots = new(snapshots);
        public InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started) =>
            _snapshots.Count > 0 ? _snapshots.Dequeue() : snapshots[^1];
        public string? FindPiSessionDirectory(string root, string sessionId) => null;
    }

    sealed class TracingControl(IHerdrAgentControl inner) : IHerdrAgentControl
    {
        readonly List<string> _events = [];
        int _polls;
        public string Events => string.Join(",", _events);
        public int Prompts { get; private set; }

        public async Task StartAsync(InteractiveLaunch launch, CancellationToken cancellationToken)
        {
            _events.Add("start");
            await inner.StartAsync(launch, cancellationToken);
            _events.Add("started");
        }

        public async Task PromptAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken)
        {
            _events.Add("prompt");
            Prompts++;
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
