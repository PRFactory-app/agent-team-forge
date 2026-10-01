using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.Business.Features.Recovery;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Features.Sessions;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Terminals;

public sealed class WtInteractiveBackendTests
{
    [Fact]
    public async Task Named_job_uses_display_label_and_keeps_recovery_identifier()
    {
        var tabs = new FakeTabs();
        var backend = new WtInteractiveBackend(tabs, new FakeReader(null), InteractiveAgentKind.Codex, Path.GetTempPath());
        await using var run = backend.Start(new BackendRequest("job", "corr", "work", "")
        { WorkingDirectory = Path.GetTempPath(), DisplayName = "reviewer-1" });
        await run.DeliverAsync(CancellationToken.None);
        Assert.Equal("codex: reviewer-1", tabs.Launch!.TabLabel);
        Assert.Matches("^atf[0-9a-f]{20}$", tabs.Launch.AgentName);
    }

    [Fact]
    public async Task Claude_synthetic_login_error_fails_bound_wt_job()
    {
        using var f = new JobFixture();
        using var transcript = new ClaudeApiErrorTranscript();
        var job = f.Submit("wt-api-login");
        var claim = f.Store.BeginNextAttempt()!;
        transcript.Write(claim.Correlation, endTurn: true);
        var backend = new WtInteractiveBackend(new FakeTabs(), transcript.Reader, InteractiveAgentKind.Claude, Path.GetTempPath());
        using var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await dispatcher.RunAttemptAsync(claim, deadline.Token);

        var record = f.Store.GetJob(job.JobId)!;
        Assert.Equal(JobStatus.Failed, record.Status);
        Assert.Equal("agent_login_required", record.ReasonCode);
        Assert.Contains("run `claude` and /login", record.ResultText);
        Assert.Equal("claude-native", record.SessionId);
    }

    [Fact]
    public async Task Live_claude_limit_keeps_wt_tab_until_later_completion()
    {
        var details = "usage limit reached; resets at 2099-09-27T18:20:00Z";
        var reader = new MutableReader(new InteractiveTranscript("claude-native", details,
            [details], ApiError: new InteractiveApiError("agent_rate_limited", details, TurnEnded: true)));
        var tabs = new FakeTabs();
        var backend = new WtInteractiveBackend(tabs, reader, InteractiveAgentKind.Claude, Path.GetTempPath());
        await using var run = backend.Start(new BackendRequest("job", "corr", "work", "") { WorkingDirectory = Path.GetTempPath() });
        await run.DeliverAsync(CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var evidence = run.ReadEvidenceAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
        Assert.True(await evidence.MoveNextAsync()); // Ack
        Assert.True(await evidence.MoveNextAsync()); // Session
        Assert.True(await evidence.MoveNextAsync());
        Assert.Equal(new BackendEvidence.AccountLimit(details), evidence.Current);
        Assert.False(tabs.Stopped);

        reader.Output = new InteractiveTranscript("claude-native", "DONE", [details, "DONE"], Completed: true);
        Assert.True(await evidence.MoveNextAsync());
        Assert.Equal(new BackendEvidence.Result("corr", "DONE"), evidence.Current);
        Assert.False(tabs.Stopped);
    }

    [Fact]
    public async Task Config_preflight_fails_before_any_tab_and_does_not_fence_the_job()
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var tabs = new FakeTabs();
        var backend = new WtInteractiveBackend(tabs, new FakeReader(null), InteractiveAgentKind.Claude,
            state.Path, "wt", configPreflight: (kind, cwd) =>
            {
                var env = new Dictionary<string, string?> { ["HOME"] = state.Path };
                if (InteractiveAgentPreflight.Check(kind, name => env.GetValueOrDefault(name), cwd, InteractivePlatform.Windows) is { } blocked)
                {
                    throw blocked;
                }
            });
        var job = f.Submit("preflight");
        var claim = f.Store.BeginNextAttempt()!;
        using var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        await dispatcher.RunAttemptAsync(claim, TestContext.Current.CancellationToken);

        Assert.True(tabs.Preflighted);
        Assert.Null(tabs.Launch);
        Assert.Equal(JobStatus.Failed, f.Store.GetJob(job.JobId)!.Status);
        Assert.Equal("agent_first_run_required", f.Store.GetJob(job.JobId)!.ReasonCode);
        Assert.False(f.Store.IsSessionFenced(job.JobId));
        Assert.Null(f.Store.GetRuns(job.JobId).Single().SubmittedAt);
    }

    [Fact]
    public async Task FakeTabLaunchRetainsNativeSessionUntilStopAgent()
    {
        var tabs = new FakeTabs();
        var reader = new FakeReader(new InteractiveTranscript("session-1", "done", Completed: true));
        var backend = new WtInteractiveBackend(tabs, reader, InteractiveAgentKind.Codex, Path.GetTempPath());
        await using var run = backend.Start(new BackendRequest("job", "corr", "say done", "") { WorkingDirectory = Path.GetTempPath() });
        await run.DeliverAsync(CancellationToken.None);
        var evidence = new List<BackendEvidence>();
        await foreach (var item in run.ReadEvidenceAsync(CancellationToken.None))
        {
            evidence.Add(item);
        }

        Assert.True(tabs.Preflighted);
        Assert.Equal(4242, run.ProcessId);
        Assert.Contains("atf-corr:corr", tabs.Prompt);
        Assert.Contains(new BackendEvidence.Session("corr", "session-1"), evidence);
        Assert.Contains(new BackendEvidence.Result("corr", "done"), evidence);
        await run.DisposeAsync();
        Assert.False(tabs.Stopped);
        Assert.True(backend.StopIdleSession("session-1"));
        Assert.True(tabs.Stopped);
    }

    [Fact]
    public async Task FailedTabLaunchNeverReportsDelivery()
    {
        var tabs = new FakeTabs { FailLaunch = true };
        var backend = new WtInteractiveBackend(tabs, new FakeReader(null), InteractiveAgentKind.Claude, Path.GetTempPath(), "wt", TimeSpan.FromMilliseconds(20));
        await using var run = backend.Start(new BackendRequest("job", "corr", "work", "") { WorkingDirectory = Path.GetTempPath() });
        await run.DeliverAsync(CancellationToken.None);
        var evidence = new List<BackendEvidence>();
        await foreach (var item in run.ReadEvidenceAsync(CancellationToken.None))
        {
            evidence.Add(item);
        }

        Assert.Equal([new BackendEvidence.ProtocolError("interactive_delivery_not_confirmed")], evidence);
    }

    [Fact]
    public async Task UnverifiedLivePidDoesNotTimeOutDelivery()
    {
        var tabs = new FakeTabs { Unverified = true };
        var backend = new WtInteractiveBackend(tabs, new UnverifiedReader(tabs), InteractiveAgentKind.Claude,
            Path.GetTempPath(), "wt", TimeSpan.FromMilliseconds(20));
        await using var run = backend.Start(new BackendRequest("job", "corr", "work", "") { WorkingDirectory = Path.GetTempPath() });
        await run.DeliverAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var evidence = new List<BackendEvidence>();
        await foreach (var item in run.ReadEvidenceAsync(cancellation.Token))
        {
            evidence.Add(item);
        }
        Assert.Contains(new BackendEvidence.Result("corr", "done"), evidence);
        Assert.True(tabs.UnverifiedChecks >= 2);
    }

    [Theory]
    [InlineData(false, "launch_failed", "not a valid application")]
    [InlineData(true, "backend_not_started", "terminal launcher is unavailable")]
    public async Task WrapperStartFailureFailsJobWithoutFence(bool notStarted, string reason, string details)
    {
        using var f = new JobFixture();
        var job = f.Submit("wt-failed-start");
        var claim = f.Store.BeginNextAttempt()!;
        var tabs = notStarted ? new FakeTabs { NotStarted = "terminal launcher is unavailable" }
            : new FakeTabs { Failure = "The specified executable is not a valid application" };
        var backend = new WtInteractiveBackend(tabs, new FakeReader(null), InteractiveAgentKind.Claude,
            Path.GetTempPath(), "wt", TimeSpan.FromSeconds(2));
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var log = new List<string>();
        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, log.Add);

        await dispatcher.RunAttemptAsync(claim, CancellationToken.None);

        var record = f.Store.GetJob(job.JobId)!;
        Assert.Equal(JobStatus.Failed, record.Status);
        Assert.Equal(reason, record.ReasonCode);
        Assert.Contains(details, record.ResultText);
        Assert.False(f.Store.IsSessionFenced(job.JobId));
        Assert.Equal(!notStarted, tabs.Stopped);
        // Like a failed preflight, the daemon log names the job and why it never started, once.
        var line = Assert.Single(log, l => l.Contains(job.JobId, StringComparison.Ordinal));
        Assert.Contains(reason + ": ", line);
        Assert.Contains(details, line);
    }

    [Fact]
    public async Task Agent_sign_in_refusal_before_acknowledgement_fails_fast_and_closes_the_tab()
    {
        using var f = new JobFixture();
        var job = f.Submit("wt-signed-out");
        var claim = f.Store.BeginNextAttempt()!;
        var tabs = new FakeTabs { Login = BackendLoginErrors.Inspect("pi", "No models available. Use /login to log into a provider via OAuth or API key.") };
        var backend = new WtInteractiveBackend(tabs, new FakeReader(null), InteractiveAgentKind.Pi,
            Path.GetTempPath(), "terminal", TimeSpan.FromMinutes(3));
        using var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await dispatcher.RunAttemptAsync(claim, deadline.Token);

        var record = f.Store.GetJob(job.JobId)!;
        Assert.Equal(JobStatus.Failed, record.Status);
        Assert.Equal("agent_login_required", record.ReasonCode);
        Assert.Contains("run `pi` and /login", record.ResultText);
        Assert.False(f.Store.IsSessionFenced(job.JobId));
        Assert.True(tabs.Stopped);
    }

    [Fact]
    public void Retained_agent_outlives_a_daemon_restart_and_is_stopped_through_its_wrapper_identity()
    {
        // Windows ownership (wrapper PID + creation time) with a real process, so it runs on every OS.
        using var state = new TempStateDir();
        Directory.CreateDirectory(state.File("wt"));
        var launch = new InteractiveLaunch(InteractiveAgentKind.Pi, "atftest", state.Path, null, null, Path.Combine(state.Path, "wt", "atftest.launch.ps1"));
        File.WriteAllText(launch.BootstrapPath, "wrapper");
        // Off Windows the stand-in is reparented away from the test host, so its exit is reaped at once (no zombie).
        using var wrapper = OperatingSystem.IsWindows()
            ? Process.Start(new ProcessStartInfo("ping", "-n 60 127.0.0.1") { UseShellExecute = false, RedirectStandardOutput = true })!
            : Detached("/bin/sleep 60");
        try
        {
            File.WriteAllText(Path.ChangeExtension(launch.BootstrapPath, ".pid"), $"{wrapper.Id}|{wrapper.StartTime.ToUniversalTime().Ticks}");
            var tabs = new WtTabControl();
            Assert.True(tabs.IsAlive(launch));
            tabs.Retained(launch, "session-1");
            var (sessionId, survivor) = Assert.Single(WtTabControl.Survivors(state.Path, InteractiveAgentKind.Pi));
            Assert.Equal("session-1", sessionId);
            Assert.Equal(launch.BootstrapPath, survivor.BootstrapPath);
            Assert.Empty(WtTabControl.Survivors(state.Path, InteractiveAgentKind.Codex));

            // A restarted daemon adopts it: follow-up, Stop agent and idle close reach the live agent.
            using var restarted = new WtInteractiveBackend(InteractiveAgentKind.Pi, state.Path);
            Assert.True(restarted.HasIdleSession("session-1"));
            Assert.True(restarted.StopIdleSession("session-1"));
            Assert.True(wrapper.WaitForExit(5000));
            Assert.Empty(WtTabControl.Survivors(state.Path, InteractiveAgentKind.Pi));
            Assert.False(File.Exists(Path.ChangeExtension(launch.BootstrapPath, ".session")));
            Assert.False(File.Exists(launch.BootstrapPath));
        }
        finally
        {
            if (!wrapper.HasExited) { wrapper.Kill(); }
        }
    }

    static Process Detached(string command)
    {
        using var shell = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", command + " >/dev/null 2>&1 & echo $!"])
        { UseShellExecute = false, RedirectStandardOutput = true })!;
        var pid = int.Parse(shell.StandardOutput.ReadLine()!, System.Globalization.CultureInfo.InvariantCulture);
        shell.WaitForExit();
        return Process.GetProcessById(pid);
    }

    [Fact]
    public void Gone_wrapper_is_never_adopted()
    {
        using var state = new TempStateDir();
        Directory.CreateDirectory(state.File("wt"));
        var wrapper = Path.Combine(state.Path, "wt", "atftest.launch.ps1");
        File.WriteAllText(Path.ChangeExtension(wrapper, ".pid"), $"{int.MaxValue}|{DateTime.UtcNow.Ticks}");
        File.WriteAllText(Path.ChangeExtension(wrapper, ".session"), "Pi\nsession-1");
        Assert.Empty(WtTabControl.Survivors(state.Path, InteractiveAgentKind.Pi));
        using var restarted = new WtInteractiveBackend(InteractiveAgentKind.Pi, state.Path);
        Assert.False(restarted.HasIdleSession("session-1"));
    }

    [Fact]
    public async Task WrapperExitWithoutStartErrorIsUncertainAndFenced()
    {
        using var f = new JobFixture();
        var job = f.Submit("wt-exited-wrapper");
        var claim = f.Store.BeginNextAttempt()!;
        // The agent may have run with the prompt in argv; its exit alone is not "no effect".
        var tabs = new FakeTabs { Exited = true };
        var backend = new WtInteractiveBackend(tabs, new FakeReader(null), InteractiveAgentKind.Claude,
            Path.GetTempPath(), "wt", TimeSpan.FromMinutes(5));
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });

        await dispatcher.RunAttemptAsync(claim, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var record = f.Store.GetJob(job.JobId)!;
        Assert.Equal(JobStatus.NeedsReconciliation, record.Status);
        Assert.Equal("interactive_agent_exited", record.ReasonCode);
    }

    [Fact]
    public void InvalidWindowsAgentImageIsRejectedBeforeTabLaunch()
    {
        using var state = new TempStateDir();
        var bogus = Path.Combine(state.Path, "claude.exe");
        File.WriteAllBytes(bogus, [0x4d, 0x5a, 0, 0]);
        Assert.Throws<BackendNotStartedException>(() => WtTabControl.ValidateWindowsAgentBinary(bogus));
        Assert.Throws<BackendNotStartedException>(() => WtTabControl.ValidateWindowsAgentBinary(Path.Combine(state.Path, "missing.exe")));
    }

    [Fact]
    public void PreflightFailureDoesNotOpenTab()
    {
        var tabs = new FakeTabs { FailPreflight = true };
        var backend = new WtInteractiveBackend(tabs, new FakeReader(null), InteractiveAgentKind.Claude, Path.GetTempPath());
        Assert.Throws<BackendNotStartedException>(() => backend.Start(new BackendRequest("job", "corr", "work", "")
        { WorkingDirectory = Path.GetTempPath() }));
        Assert.Null(tabs.Launch);
    }

    [Fact]
    public async Task LateTabLaunchCompletesAfterCorrelatedNativeRecord()
    {
        var tabs = new LateSidecarTabs();
        var reader = new SidecarReader(tabs);
        var backend = new WtInteractiveBackend(tabs, reader, InteractiveAgentKind.Codex,
            Path.GetTempPath(), "wt", TimeSpan.FromSeconds(2));
        await using var run = backend.Start(new BackendRequest("job", "corr", "work", "") { WorkingDirectory = Path.GetTempPath() });
        await run.DeliverAsync(CancellationToken.None);
        Assert.Null(run.ProcessId); // The launch wait expired before its sidecar appeared.
        var evidence = new List<BackendEvidence>();
        await foreach (var item in run.ReadEvidenceAsync(CancellationToken.None)) { evidence.Add(item); }
        Assert.Equal(4242, run.ProcessId);
        Assert.Equal(1, tabs.Starts);
        Assert.Contains(new BackendEvidence.Ack("corr"), evidence);
        Assert.Contains(new BackendEvidence.Result("corr", "finished"), evidence);
        Assert.DoesNotContain(evidence, item => item is BackendEvidence.ProtocolError);
    }

    [Fact]
    public async Task FencedWtJobStopsOnlyItsOwnedTab()
    {
        using var f = new JobFixture();
        var job = f.Submit("wt-fenced");
        var claim = f.Store.BeginNextAttempt()!;
        var tabs = new FakeTabs();
        var backend = new WtInteractiveBackend(tabs, new FakeReader(null), InteractiveAgentKind.Codex,
            Path.GetTempPath(), "wt", TimeSpan.FromMilliseconds(20));
        var run = backend.Start(new BackendRequest(job.JobId, claim.Correlation, "work", "") { WorkingDirectory = Path.GetTempPath() });
        await run.DeliverAsync(CancellationToken.None);
        await run.DisposeAsync();
        Assert.True(f.Store.EndUnsuccessfully(new RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation),
            JobStatus.NeedsReconciliation, "interactive_delivery_not_confirmed"));
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None,
            f.Admission, _ => { });
        var stopped = new StopJob(f.Store, JobFixture.Operator, dispatcher.CancelRunning,
            stopReconciled: dispatcher.StopReconciled).Execute(job.JobId);
        Assert.Equal("stopped", stopped.Outcome);
        Assert.True(tabs.Stopped);
        Assert.False(f.Store.IsSessionFenced(job.JobId));
    }

    [Fact]
    public async Task IntermediateAssistantTextIsNotReportedAsFinishedTurn()
    {
        var tabs = new FakeTabs();
        var reader = new SequenceReader(
            new InteractiveTranscript("session-1", "working", Completed: false),
            new InteractiveTranscript("session-1", "finished", Completed: true));
        var backend = new WtInteractiveBackend(tabs, reader, InteractiveAgentKind.Claude, Path.GetTempPath());
        await using var run = backend.Start(new BackendRequest("job", "corr", "work", "") { WorkingDirectory = Path.GetTempPath() });
        await run.DeliverAsync(CancellationToken.None);
        var evidence = new List<BackendEvidence>();
        await foreach (var item in run.ReadEvidenceAsync(CancellationToken.None))
        {
            evidence.Add(item);
        }
        Assert.Contains(new BackendEvidence.Result("corr", "finished"), evidence);
        Assert.DoesNotContain(new BackendEvidence.Result("corr", "working"), evidence);
    }

    [Fact]
    public void WrapperKeepsPromptAndUsesResumeWithoutExposingItToWt()
    {
        using var state = new TempStateDir();
        var cwd = Path.Combine(state.Path, "work's repo");
        Directory.CreateDirectory(cwd);
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", cwd, "native-1", null, "C:\\state\\tab.ps1");
        var prompt = "first line; it's fine\nsecond — line";
        var wrapper = Encoding.UTF8.GetString(WtTabControl.WrapperBytes(launch, prompt, "C:\\state\\tab.pid", @"C:\daemon\codex"));
        Assert.StartsWith("\uFEFF", wrapper);
        Assert.Contains("$start.FileName = 'codex'", wrapper);
        Assert.Contains("$start.Arguments = '--dangerously-bypass-approvals-and-sandbox -C", wrapper);
        Assert.Contains("resume native-1", wrapper);
        Assert.Contains("first line; it''s fine\nsecond — line", wrapper);
        Assert.Contains("$start.WorkingDirectory = '" + cwd.Replace("'", "''") + "'", wrapper);
        Assert.Contains("$env:CODEX_HOME = 'C:\\daemon\\codex'", wrapper);
        Assert.Contains("$PID.ToString() + '|'", wrapper);
        Assert.Contains("WriteAllText('C:\\state\\tab.start-error'", wrapper);
        Assert.Contains("[uint32]0x2000", wrapper);
        Assert.Contains("AssignProcessToJobObject($job, $agent.Handle)", wrapper);
        Assert.Contains("if (-not $native::AssignProcessToJobObject", wrapper);
        Assert.Contains("$agent.Kill()", wrapper);
        Assert.Contains("finally { [void]$native::CloseHandle($job) }", wrapper);
        // PowerShell only ends a here-string at a line-initial '@; a stray indent breaks every launch.
        Assert.Matches(@"\$source = @'\r?\n", wrapper);
        Assert.Matches(@"\n'@\r?\n", wrapper);
        // A $null argument would reach a .NET string parameter as "" rather than NULL.
        Assert.Contains("CreateJobObject([IntPtr]::Zero, [IntPtr]::Zero)", wrapper);
        // The agent identity lets stop end the agent first so the wrapper exits 0 and its tab closes.
        Assert.Contains("Out-File -FilePath 'C:\\state\\tab.agent' -Encoding ascii", wrapper);
        Assert.DoesNotContain("__AGENT_SIDECAR__", wrapper);
        Assert.DoesNotContain("__START_ERROR__", wrapper);
        Assert.EndsWith("exit 0\r\n", wrapper);
    }

    [Fact]
    public void RecordedWrapperFailureAndExitedPidAreObservedWithoutAWindowProbe()
    {
        using var state = new TempStateDir();
        var wrapper = Path.Combine(state.Path, "tab.launch.ps1");
        var launch = new InteractiveLaunch(InteractiveAgentKind.Claude, "atftest", state.Path, null, null, wrapper);
        var sidecar = Path.ChangeExtension(wrapper, ".pid");
        var tabs = new WtTabControl();
        File.WriteAllText(sidecar, $"{int.MaxValue}|{DateTime.UtcNow.Ticks}");
        Assert.True(tabs.WrapperExited(launch));
        Assert.Null(tabs.StartFailure(launch));
        File.WriteAllText(Path.ChangeExtension(wrapper, ".start-error"), "bad image");
        Assert.Equal("interactive agent could not start: bad image", tabs.StartFailure(launch));
        tabs.StopOwned(launch);
        Assert.False(File.Exists(sidecar));
    }

    [Fact]
    public void FreshWindowsCodexDirectoryIsTrustedOnlyInTheLaunchArguments()
    {
        using var state = new TempStateDir();
        var cwd = Path.Combine(state.Path, "New Dir");
        Directory.CreateDirectory(cwd);
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", cwd, null, null, "C:\\state\\tab.ps1");
        var args = WtTabControl.AgentArguments(launch, "task");

        Assert.Equal(["-C", cwd], args.SkipWhile(arg => arg != "-C").Take(2));
        Assert.Equal(["-c", "projects={'" + CodexPaths.TrustKey(cwd) + "'={trust_level='trusted'}}"],
            args.SkipWhile(arg => arg != "-c").Take(2));
        var wrapper = Encoding.UTF8.GetString(WtTabControl.WrapperBytes(launch, "task", "C:\\state\\tab.pid"));
        Assert.Contains("projects={", wrapper);
        Assert.DoesNotContain("CLAUDE_CODE_SANDBOXED =", wrapper);
    }

    [Fact]
    public void FreshWindowsClaudeDirectoryGetsPerLaunchTrustAndBypass()
    {
        var launch = new InteractiveLaunch(InteractiveAgentKind.Claude, "atftest", "C:\\code\\new dir", null, null, "C:\\state\\tab.ps1");
        var args = WtTabControl.AgentArguments(launch, "task");

        Assert.Equal(["--permission-mode", "bypassPermissions", "--settings", "{\"skipDangerousModePermissionPrompt\":true}"],
            args.Skip(1).Take(4));
        var wrapper = Encoding.UTF8.GetString(WtTabControl.WrapperBytes(launch, "task", "C:\\state\\tab.pid"));
        Assert.True(wrapper.IndexOf("$env:CLAUDE_CODE_SANDBOXED = '1'", StringComparison.Ordinal) >
            wrapper.IndexOf("Remove-Item -LiteralPath", StringComparison.Ordinal));
        Assert.Contains("'CLAUDE_CODE_SESSION_ID'", wrapper);
        Assert.Contains("$_.Name.StartsWith('CLAUDE_CODE_MESSAGING_'", wrapper);
        Assert.DoesNotContain("-match '^(CLAUDE_CODE_", wrapper);
        Assert.DoesNotContain("'CLAUDE_CODE_GIT_BASH_PATH'", wrapper);
        Assert.Contains("$start.Arguments = ", wrapper);
    }

    [Theory]
    [InlineData(InteractiveAgentKind.Claude, "--resume")]
    [InlineData(InteractiveAgentKind.Codex, "resume")]
    [InlineData(InteractiveAgentKind.Pi, "--continue")]
    public void ManagedWtLaunchAndResumeCarryPrivateMcpConfig(InteractiveAgentKind kind, string resumeFlag)
    {
        using var state = new TempStateDir();
        var root = Path.Combine(state.Path, "state with spaces");
        var configPath = ManagedChildContext.ConfigPath(root, "job-first");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ManagedChildContext.ServerName] = new JsonObject
                {
                    ["command"] = @"C:\Program Files\ATF\atf.exe",
                    ["args"] = new JsonArray("mcp", "--state-dir", root, "--managed-context", Path.Combine(root, "parent context.json")),
                },
            },
        }.ToJsonString();
        File.WriteAllText(configPath, config);
        var followConfig = ManagedChildContext.ConfigPath(root, "job-follow");
        Directory.CreateDirectory(Path.GetDirectoryName(followConfig)!);
        File.WriteAllText(followConfig, config);
        var initial = new InteractiveLaunch(kind, "atffirst", root, null, Path.Combine(root, "pi"),
            Path.Combine(root, "wt", "first.launch.ps1"))
        { JobId = "job-first" };
        var follow = initial with
        {
            AgentName = "atffollow",
            ResumeSessionId = "native-1",
            BootstrapPath = Path.Combine(root, "wt", "follow.launch.ps1"),
            JobId = "job-follow"
        };

        foreach (var launch in new[] { initial, follow })
        {
            var args = WtTabControl.AgentArguments(launch, "task");
            var expected = ManagedChildContext.Arguments(kind.ToString().ToLowerInvariant(),
                ManagedChildContext.ConfigPath(root, launch.JobId!));
            Assert.Equal(expected, args.Skip(1).Take(expected.Count));
            if (kind == InteractiveAgentKind.Codex)
            {
                var serverArgs = Assert.Single(args, arg => arg.StartsWith("mcp_servers.agentteamforge.args=", StringComparison.Ordinal));
                Assert.Contains(Path.Combine(root, "parent context.json"), serverArgs);
                Assert.Contains("\\\"mcp\\\"", WtTabControl.CommandLine([serverArgs]));
                Assert.Contains("\\\"mcp\\\"", WindowsCliLaunch.ShimArgument(serverArgs));
            }
            else { Assert.Contains(ManagedChildContext.ConfigPath(root, launch.JobId!), args); }
            var wrapper = Encoding.UTF8.GetString(WtTabControl.WrapperBytes(launch, "task", Path.Combine(root, "wt", "tab.pid")));
            if (kind == InteractiveAgentKind.Pi) { Assert.Contains("$env:PI_MCP_CONFIG_MODE = 'exclusive'", wrapper); }
        }
        Assert.Contains(resumeFlag, WtTabControl.AgentArguments(follow, "task"));
    }

    [Theory]
    [InlineData(InteractiveAgentKind.Claude, "model=opus;effort=high", "--model", "opus", "--effort", "high")]
    [InlineData(InteractiveAgentKind.Codex, "model=gpt-6-sol;effort=xhigh", "-m", "gpt-6-sol", "-c", "model_reasoning_effort=\"xhigh\"")]
    [InlineData(InteractiveAgentKind.Pi, "model=gpt-6-luna;effort=max", "--model", "openai-codex/gpt-6-luna", "--thinking", "max")]
    public async Task TabLaunchAndResumeCarryResolvedSelection(InteractiveAgentKind kind, string options,
        string modelFlag, string model, string effortFlag, string effort)
    {
        var tabs = new FakeTabs();
        var backend = new WtInteractiveBackend(tabs, new FakeReader(null), kind, Path.GetTempPath());
        await using var run = backend.Start(new BackendRequest("job", "corr", "task", options)
        { WorkingDirectory = Path.GetTempPath(), ResumeSessionId = "native-1" });
        await run.DeliverAsync(CancellationToken.None);
        var args = WtTabControl.AgentArguments(tabs.Launch!, "task");
        Assert.Equal([modelFlag, model], args.SkipWhile(arg => arg != modelFlag).Take(2));
        Assert.Equal(effortFlag, args[args.ToList().IndexOf(effort) - 1]);
        Assert.Contains(kind switch { InteractiveAgentKind.Claude => "--resume", InteractiveAgentKind.Codex => "resume", _ => "--continue" }, args);
    }

    [Theory]
    [InlineData(InteractiveAgentKind.Claude)]
    [InlineData(InteractiveAgentKind.Codex)]
    [InlineData(InteractiveAgentKind.Pi)]
    public void LongWindowsPromptIsHandedOverAsItsFileWithTheCorrelationMarker(InteractiveAgentKind kind)
    {
        var launch = new InteractiveLaunch(kind, "atftest", Environment.CurrentDirectory, null, kind == InteractiveAgentKind.Pi ? "C:\\state\\pi" : null, "C:\\state\\tab.ps1");
        var marker = "[AgentTeamForge correlation id: atf-corr:abc123 — internal marker, ignore this line]";
        var shortArgs = WtTabControl.AgentArguments(launch, "task\n\n" + marker, windowsCommandLine: true);
        var longArgs = WtTabControl.AgentArguments(launch, new string('"', 20_000) + "\n\n" + marker, windowsCommandLine: true);

        Assert.Equal("task\n\n" + marker, shortArgs[^1]);
        Assert.True(WtTabControl.CommandLine(longArgs).Length < WtTabControl.MaxWindowsCommandLineChars);
        Assert.Contains("C:\\state\\tab.prompt.txt", longArgs[^1]);
        if (kind == InteractiveAgentKind.Pi) { Assert.Equal("@C:\\state\\tab.prompt.txt", longArgs[^1]); }
        else { Assert.EndsWith(" " + marker, longArgs[^1]); }
    }

    [Fact]
    public void WindowsHookAndShimArgumentsKeepThePromptOutOfCmd()
    {
        var hooks = WtTabControl.CodexHookArguments("C:\\state\\codex-hook.cmd");
        Assert.Contains(hooks, value => value.Contains("commandWindows='C:\\state\\codex-hook.cmd'", StringComparison.Ordinal));
        Assert.Contains("--dangerously-bypass-hook-trust", hooks);
        Assert.Equal("@C:\\state\\task.txt", WtTabControl.ShimPrompt(InteractiveAgentKind.Pi, "C:\\state\\task.txt"));
        Assert.DoesNotContain('\n', WtTabControl.ShimPrompt(InteractiveAgentKind.Claude, "C:\\state\\task.txt"));
    }

    [Fact]
    public void HeadlessShimCommandKeepsArgumentsSeparateFromInstructionStdin()
    {
        var command = WindowsCliLaunch.PowerShellCommand("C:\\Program Files\\pi.cmd", ["--model", "a model"]);
        Assert.Contains("& 'C:\\Program Files\\pi.cmd' '--model' 'a model'", command);
        Assert.DoesNotContain("task instruction", command);
    }

    [Theory]
    [InlineData("gpt&calc")]
    [InlineData("C:\\repo|x")]
    [InlineData("%USERPROFILE%")]
    [InlineData("a^b")]
    [InlineData("line\nbreak")]
    public void CmdShimArgumentsWithCmdMetacharactersAreRejected(string value) =>
        Assert.Throws<BackendNotStartedException>(() => WindowsCliLaunch.EnsureCmdSafe(["--model", value]));

    [Theory]
    [InlineData("{\"skipDangerousModePermissionPrompt\":true}", "{\\\"skipDangerousModePermissionPrompt\\\":true}")]
    [InlineData("model_reasoning_effort=\"high\"", "model_reasoning_effort=\\\"high\\\"")]
    [InlineData("projects={'C:\\a b'={trust_level='trusted'}}", "projects={'C:\\a b'={trust_level='trusted'}}")]
    public void CmdShimArgumentsKeepEmbeddedQuotesThroughPowerShell(string value, string expected)
    {
        Assert.Equal(expected, WindowsCliLaunch.ShimArgument(value));
        Assert.Contains(PowerShellText.Quote(expected), WindowsCliLaunch.PowerShellCommand("C:\\n\\claude.cmd", [value]));
    }

    [Fact]
    public void CmdShimAcceptsOrdinaryArguments() =>
        WindowsCliLaunch.EnsureCmdSafe(["-c", "model_reasoning_effort=\"high\"", "C:\\Users\\A B\\repo", "hooks.Stop=[{hooks=[{type='command'}]}]"]);

    [Fact]
    public void RecoveryRequiresCreationTimeAlongsidePid()
    {
        var sidecar = Path.Combine(Path.GetTempPath(), "atf-sidecar-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(sidecar, "4242");
            Assert.Null(WtTabControl.TryReadOwned(sidecar, "wrapper.ps1"));
            File.WriteAllText(sidecar, "4242|638945424000000000");
            var owned = WtTabControl.TryReadOwned(sidecar, "wrapper.ps1");
            Assert.Equal(4242, owned?.Pid);
            Assert.Equal(new DateTime(638945424000000000, DateTimeKind.Utc), owned?.Created);
        }
        finally { File.Delete(sidecar); }
    }

    [Fact]
    public void RestartFindsOnlyTheMatchingOwnedJob()
    {
        var root = Path.Combine(Path.GetTempPath(), "atf-wt-recovery-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "wt");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "atfforeign.launch.job"), "other-job");
            File.WriteAllText(Path.Combine(directory, "atfowned.launch.job"), "our-job");
            var found = WtInteractiveBackend.FindRecoveredLaunch(root, InteractiveAgentKind.Codex, "our-job");
            Assert.Equal("atfowned", found?.AgentName);
            Assert.Equal(Path.Combine(directory, "atfowned.launch.ps1"), found?.BootstrapPath);
            Assert.Null(WtInteractiveBackend.FindRecoveredLaunch(root, InteractiveAgentKind.Codex, "missing-job"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RestartQuarantinesRunningWtJobForVerifiedStop()
    {
        using var fixture = new JobFixture();
        var job = fixture.Submit("wt-restart");
        _ = fixture.Store.BeginNextAttempt();
        var quarantined = new RecoverOnStartup(fixture.NewStore()).Execute();
        Assert.Contains(job.JobId, quarantined);
        Assert.Equal(JobStatus.NeedsReconciliation, fixture.Store.GetJob(job.JobId)?.Status);
    }

    [Fact]
    public void WindowsLaunchersRequestBreakawayButKeepWrapperCleanupLocal()
    {
        Assert.Equal(WindowsConsoleProcess.BreakawayFromJob | WindowsConsoleProcess.NewProcessGroup | WindowsConsoleProcess.NoWindow,
            WindowsConsoleProcess.CreationFlags(newConsole: false));
        Assert.Equal(WindowsConsoleProcess.BreakawayFromJob | WindowsConsoleProcess.NewProcessGroup | WindowsConsoleProcess.NewConsole,
            WindowsConsoleProcess.CreationFlags(newConsole: true));
        var launch = new InteractiveLaunch(InteractiveAgentKind.Claude, "atftest", "C:\\work", null, null, "C:\\state\\tab.ps1");
        var wrapper = Encoding.UTF8.GetString(WtTabControl.WrapperBytes(launch, "task", "C:\\state\\tab.pid"));
        Assert.Contains("finally { [void]$native::CloseHandle($job) }", wrapper);
        Assert.Contains("Out-File -FilePath 'C:\\state\\tab.agent' -Encoding ascii", wrapper);
    }

    [Fact]
    public void TypographicQuotesCannotEndThePowerShellLiteral()
    {
        // PowerShell treats U+2018..U+201B as single quotes; undoubled they would end the
        // literal and run the rest of an untrusted prompt as script.
        Assert.Equal("'a\u2019\u2019; calc; \u2018\u2018b''c\u201A\u201A\u201B\u201B'",
            PowerShellText.Quote("a\u2019; calc; \u2018b'c\u201A\u201B"));
    }

    [Fact]
    public void NativeCommandLineKeepsQuotesAndBackslashesInOneArgument()
    {
        // Parsed back by CommandLineToArgvW: 2n backslashes + quote -> n and a delimiter,
        // 2n+1 + quote -> n and a literal quote; other backslashes are literal.
        Assert.Equal("plain \"\" \"say \\\"hi\\\" --flag \\\\\\\"x\" \"C:\\dir with space\\\\\"",
            WtTabControl.CommandLine(["plain", "", "say \"hi\" --flag \\\"x", "C:\\dir with space\\"]));
    }

    [Theory]
    [InlineData(";", @"\;")]
    [InlineData(@"\;", @"\\;")]
    [InlineData(@"C:\p;calc.exe", @"C:\p\;calc.exe")]
    [InlineData(@"C:\p\;q", @"C:\p\\;q")]
    [InlineData("a;b;c", @"a\;b\;c")]
    public void WtOptionDelimitersStayInOneCommand(string value, string expected) =>
        Assert.Equal(expected, WtCommandLine.EscapeDelimiter(value));

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("has space", "has space")]
    [InlineData("tab\there", "\"tab\there\"")]
    [InlineData("quote\"inside", "\"quote\\\"inside\"")]
    [InlineData("quote \"and space\"", "quote \\\"and space\\\"")]
    [InlineData(@"a b\", @"a b\\")]
    [InlineData("semi;colon", @"semi\;colon")]
    [InlineData("mixed \"q\"; next", "mixed \\\"q\\\"\\; next")]
    [InlineData("it's ’smart’", "it's ’smart’")]
    [InlineData("multi\nline", "\"multi\nline\"")]
    public void WtChildArgumentsPreserveWindowsQuotingAndUnicode(string value, string expected) =>
        Assert.Equal(expected, WtCommandLine.ChildArgument(value));

    [Fact]
    public void WtLaunchEscapesTitleAndWrapperPathButKeepsThemSeparate()
    {
        var args = WtCommandLine.Arguments(
            ["nt", "--title", "a;\"b", "-d", @"C:\é dir;test"],
            ["powershell.exe", "-File", @"C:\state dir;test\tab.launch.ps1"]);

        Assert.Equal(["nt", "--title", "a\\;\"b", "-d", @"C:\é dir\;test",
            "--", "powershell.exe", "-File", @"C:\state dir\;test\tab.launch.ps1"], args);
    }

    [Theory]
    [InlineData(new[] { "50% done" }, false)]
    [InlineData(new[] { "%USERPROFILE%" }, true)]
    [InlineData(new[] { "50%", "then 80%" }, true)]
    [InlineData(new[] { "%", "%" }, true)]
    public void WtExpansionGuardCountsPercentAcrossChildArguments(string[] child, bool expected) =>
        Assert.Equal(expected, WtCommandLine.MayExpand(child));

    [Fact]
    public void UnsafeWtWrapperPathSelectsTheInteractiveConsoleFallback()
    {
        var options = new[] { "nt", "--title", "%TITLE%" };
        Assert.NotNull(WtCommandLine.Arguments(options, ["powershell.exe", "-File", @"C:\state\50% done\tab.ps1"]));
        Assert.Null(WtCommandLine.Arguments(options, ["powershell.exe", "-File", @"C:\state\%USERPROFILE%\tab.ps1"]));
        Assert.Equal("'literal $env:USERPROFILE and %USERPROFILE%'",
            PowerShellText.Quote("literal $env:USERPROFILE and %USERPROFILE%"));
    }

    [Fact]
    public void PiTranscriptNeedsFinalStopReason()
    {
        var root = Path.Combine(Path.GetTempPath(), "atf-wt-transcript-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "session.jsonl");
            File.WriteAllLines(path,
            [
                """{"type":"session","id":"pi-native"}""",
                """{"type":"message","message":{"role":"user","content":[{"type":"text","text":"atf-corr:turn"}]}}""",
                """{"type":"message","message":{"role":"assistant","stopReason":"toolUse","content":[{"type":"text","text":"working"}]}}""",
            ]);
            var launch = new InteractiveLaunch(InteractiveAgentKind.Pi, "atftest", root, null, root, Path.Combine(root, "tab.ps1"));
            var reader = new InteractiveTranscriptReader();
            var partial = reader.Read(launch, "atf-corr:turn", DateTimeOffset.UtcNow);
            Assert.False(partial?.Completed);
            File.AppendAllText(path, """{"type":"message","message":{"role":"assistant","stopReason":"stop","content":[{"type":"text","text":"finished"}]}}""" + "\n");
            var final = reader.Read(launch, "atf-corr:turn", DateTimeOffset.UtcNow);
            Assert.True(final?.Completed);
            Assert.Equal("finished", final?.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RealControlRejectsNonWindowsBeforeAnyTabStarts()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var backend = new WtInteractiveBackend(InteractiveAgentKind.Claude, Path.GetTempPath());
        Assert.Throws<BackendNotStartedException>(() => backend.Start(
            new BackendRequest("job", "corr", "work", "") { WorkingDirectory = Path.GetTempPath() }));
    }

    [Fact]
    public async Task Native_claude_rollback_and_revert_resume_one_launch_and_settle_the_captured_tab()
    {
        using var fixture = new JobFixture();
        var tabs = new TrackingTabs();
        const string session = "native-session";
        using var backend = new WtInteractiveBackend(tabs,
            new FakeReader(new InteractiveTranscript(session, "done", Completed: true)), InteractiveAgentKind.Claude, Path.GetTempPath());
        var catalog = new BackendCatalog().Register(BackendCatalog.Claude, () => backend);
        var accept = new AcceptJob(fixture.Store, JobFixture.Operator, fixture.Limits, fixture.TestProfile, fixture.Admission, catalog.Names);
        var parent = accept.Execute(new SubmitJobRequest("parent", "work", null, false) { Backend = BackendCatalog.Claude }).Job!;
        using var dispatcher = new DispatchJob(fixture.Store, catalog, fixture.Limits, DurabilityCheckpoints.None, fixture.Admission, _ => { });
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        tabs.Last!.NativeTranscript = new(session, "/fake/native.jsonl");
        var follow = new FollowUpJob(fixture.Store, JobFixture.Operator, accept);
        Assert.Equal(JobStatus.Completed, fixture.Store.GetJob(parent.JobId)!.Status);
        Assert.True(backend.HasIdleClaudeSession(session));
        var child = follow.Execute(new FollowUpRequest(parent.JobId, "next", "next")).Job!;
        Assert.Contains(";native_claude=1", fixture.Store.GetJob(child.JobId)!.Options);
        using (var connection = fixture.Database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER reject_native BEFORE INSERT ON native_claude_attempts BEGIN SELECT RAISE(ABORT, 'test rollback'); END";
            command.ExecuteNonQuery();
        }
        Assert.Throws<StorageException>(() => dispatcher.TakeNativeClaude(parent.JobId, "/fake/home"));
        Assert.True(backend.HasIdleSession(session));
        using (var connection = fixture.Database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER reject_native";
            command.ExecuteNonQuery();
        }
        var claim = dispatcher.TakeNativeClaude(parent.JobId, "/fake/home")!;
        Assert.Equal(child.JobId, claim.Job.JobId);
        var sessions = new LeadSessionStore(fixture.Database);
        var workspace = Path.GetTempPath();
        var lead = sessions.Start(workspace, "managed-child:" + parent.JobId);
        var address = OperatingSystem.IsWindows() ? @"\\.\pipe\atf-test" : "/tmp/atf-test";
        var wake = new WakeStore(fixture.Database).Register("test-channel", "claude", address, "secret", "123");
        sessions.BindWake(lead.SessionId, wake.Key, wake.Generation);
        var probes = 0;
        var endpoint = new JobsEndpoint(accept, fixture.Get(), follow, fixture.List(),
            new StopJob(fixture.Store, JobFixture.Operator, _ => { }), DurabilityCheckpoints.None, () => { },
            jobStore: fixture.Store, sessions: sessions, releaseNativeTurn: dispatcher.ReleaseNativeTurn,
            agentLive: (_, id, _) =>
            {
                probes++;
                return id is null ? null : backend.HasLiveSession(id);
            });
        var reverted = endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.ClaudeDeliveryComplete,
            JobId = child.JobId,
            LeadSessionId = lead.SessionId,
            Workspace = workspace,
            WakeKind = "claude",
            WakeAddress = address,
            WakeSecret = "secret",
            WakeHome = "123",
            NativeRunId = claim.RunId,
            NativeCorrelation = claim.Correlation,
            NativeWriteStarted = false
        });
        Assert.True(reverted.Ok);
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        Assert.Equal(2, tabs.Starts);
        Assert.Single(tabs.Live);
        tabs.Last!.NativeTranscript = new(session, "/fake/native.jsonl");
        Assert.True(backend.TakeIdleForNativeTurn(session));
        backend.RememberNativeTurn(session);
        var physicalProbes = tabs.Probes;
        var list = endpoint.Handle(new IpcRequest { Op = IpcProtocol.JobList });
        Assert.True(list.Ok);
        Assert.Equal(1, probes); // Both completed jobs share this session.
        Assert.All(list.Page!.Jobs, job => Assert.True(job.AgentLive));
        Assert.Equal(physicalProbes, tabs.Probes);
        Assert.True(backend.StopIdleSession(session));
        Assert.Empty(tabs.Live);
        Assert.Equal(2, tabs.Stops);
        Assert.False(backend.HasLiveSession(session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unresolved_native_codex_keeps_zero_timeout_tab_until_transcript_settles(bool deliveryTimeout)
    {
        using var fixture = new JobFixture();
        var tabs = new TrackingTabs();
        const string session = "codex-native";
        var settings = new InteractiveRetentionSettings();
        using var backend = new WtInteractiveBackend(tabs,
            new FakeReader(new InteractiveTranscript(session, "done", Completed: true)), InteractiveAgentKind.Codex,
            Path.GetTempPath(), retentionSettings: () => settings);
        var catalog = new BackendCatalog().Register(BackendCatalog.Codex, () => backend);
        var accept = new AcceptJob(fixture.Store, JobFixture.Operator, fixture.Limits, fixture.TestProfile, fixture.Admission, catalog.Names);
        var parent = accept.Execute(new SubmitJobRequest("parent", "work", null, false) { Backend = BackendCatalog.Codex }).Job!;
        using var dispatcher = new DispatchJob(fixture.Store, catalog, fixture.Limits, DurabilityCheckpoints.None, fixture.Admission, _ => { })
        {
            SubmitNativeCodex = (_, _, _, _) => deliveryTimeout
                ? Task.FromException<CodexSubmission>(new OperationCanceledException())
                : Task.FromResult(new CodexSubmission(true, null))
        };
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        var child = new FollowUpJob(fixture.Store, JobFixture.Operator, accept)
            .Execute(new FollowUpRequest(parent.JobId, "next", "next")).Job!;
        var home = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "codex-home");
        var claim = fixture.Store.BeginNativeCodexAttempt(job => backend.TakeIdleForNativeTurn(job.SessionId!), home)!;
        settings = settings with { IdleCloseMinutes = 0 };
        await dispatcher.RunAttemptAsync(claim, CancellationToken.None);
        Assert.Equal(JobStatus.NeedsReconciliation, fixture.Store.GetJob(child.JobId)!.Status);
        Assert.Single(tabs.Live);
        Assert.False(backend.HasIdleSession(session));

        var directory = Directory.CreateDirectory(Path.Combine(home, "sessions")).FullName;
        File.WriteAllLines(Path.Combine(directory, "rollout-test-codex-native.jsonl"),
        [
            """{"type":"session_meta","payload":{"id":"codex-native","source":"cli"}}""",
            """{"type":"event_msg","payload":{"type":"task_started"}}""",
            $$$"""{"type":"event_msg","payload":{"type":"user_message","message":"atf-corr:{{{claim.Correlation}}}"}}""",
            """{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"settled"}]}}""",
            """{"type":"event_msg","payload":{"type":"task_complete"}}"""
        ]);
        using var stopping = new CancellationTokenSource();
        var loop = dispatcher.RunAsync(stopping.Token);
        try
        {
            await Bounded.Until(() => fixture.Store.GetJob(child.JobId)?.Status == JobStatus.Completed, "native settlement");
            await Bounded.Until(() => backend.HasLiveSession(session) == false, "settled tab close");
            Assert.Empty(tabs.Live);
            Assert.False(backend.HasLiveSession(session));
            Assert.Equal(1, tabs.Stops);
        }
        finally
        {
            await stopping.CancelAsync();
            await loop.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
        }
    }

    sealed class TrackingTabs : IWtTabControl
    {
        public List<InteractiveLaunch> Live { get; } = [];
        public InteractiveLaunch? Last { get; private set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public int Probes { get; private set; }
        public void Preflight(InteractiveAgentKind kind) { }
        public Task StartAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken)
        {
            Starts++;
            Last = launch;
            Live.Add(launch);
            return Task.CompletedTask;
        }
        public bool IsAlive(InteractiveLaunch launch) { Probes++; return Live.Contains(launch); }
        public bool WrapperExited(InteractiveLaunch launch) => false;
        public string? StartFailure(InteractiveLaunch launch) => null;
        public int? ProcessId(InteractiveLaunch launch) => null;
        public void StopOwned(InteractiveLaunch launch) { if (Live.Remove(launch)) { Stops++; } }
    }

    sealed class FakeTabs : IWtTabControl
    {
        public bool Preflighted { get; private set; }
        public bool FailLaunch { get; init; }
        public bool Unverified { get; init; }
        public int UnverifiedChecks { get; private set; }
        public bool FailPreflight { get; init; }
        public string? Failure { get; init; }
        public string? NotStarted { get; init; }
        public BackendEvidence.AgentError? Login { get; init; }
        public BackendEvidence.AgentError? LoginBlocker(InteractiveLaunch launch) => Login;
        public bool Stopped { get; private set; }
        public string Prompt { get; private set; } = "";
        public InteractiveLaunch? Launch { get; private set; }
        public void Preflight(InteractiveAgentKind kind)
        {
            Preflighted = true;
            if (FailPreflight) { throw new BackendNotStartedException("agent launcher is invalid"); }
        }
        public Task StartAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken)
        {
            if (FailLaunch)
            {
                throw new IOException("tab failed");
            }
            if (NotStarted is { } notStarted)
            {
                throw new BackendNotStartedException(notStarted);
            }

            Prompt = prompt;
            Launch = launch;
            return Task.CompletedTask;
        }
        public bool IsAlive(InteractiveLaunch launch) => !Stopped;
        public bool IsUnverified(InteractiveLaunch launch)
        {
            UnverifiedChecks++;
            return Unverified;
        }
        public bool Exited { get; init; }
        public string? StartFailure(InteractiveLaunch launch) => Failure;
        public bool WrapperExited(InteractiveLaunch launch) => Exited;
        public int? ProcessId(InteractiveLaunch launch) => Prompt.Length > 0 ? 4242 : null;
        public void StopOwned(InteractiveLaunch launch) => Stopped = true;
    }

    sealed class LateSidecarTabs : IWtTabControl
    {
        int _probes;
        public int Starts { get; private set; }
        public void Preflight(InteractiveAgentKind kind) { }
        public Task StartAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken)
        {
            Starts++;
            throw new IOException("tab readiness timed out after handing off the wrapper");
        }
        public bool IsAlive(InteractiveLaunch launch) => Interlocked.Increment(ref _probes) > 2;
        public string? StartFailure(InteractiveLaunch launch) => null;
        public bool WrapperExited(InteractiveLaunch launch) => false;
        public int? ProcessId(InteractiveLaunch launch) => Volatile.Read(ref _probes) > 2 ? 4242 : null;
        public void StopOwned(InteractiveLaunch launch) { }
    }

    sealed class SidecarReader(LateSidecarTabs tabs) : IInteractiveTranscriptReader
    {
        public InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started) =>
            tabs.IsAlive(launch) ? new InteractiveTranscript("native", "finished", Completed: true) : null;
        public string? FindPiSessionDirectory(string root, string sessionId) => null;
    }

    sealed class FakeReader(InteractiveTranscript? transcript) : IInteractiveTranscriptReader
    {
        public InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started) => transcript;
        public string? FindPiSessionDirectory(string root, string sessionId) => Path.Combine(root, sessionId);
    }

    sealed class MutableReader(InteractiveTranscript? transcript) : IInteractiveTranscriptReader
    {
        public InteractiveTranscript? Output { get; set; } = transcript;
        public InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started) => Output;
        public string? FindPiSessionDirectory(string root, string sessionId) => null;
    }

    sealed class UnverifiedReader(FakeTabs tabs) : IInteractiveTranscriptReader
    {
        public InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started) =>
            tabs.UnverifiedChecks >= 2 ? new InteractiveTranscript("session-1", "done", Completed: true) : null;
        public string? FindPiSessionDirectory(string root, string sessionId) => null;
    }

    sealed class SequenceReader(params InteractiveTranscript[] snapshots) : IInteractiveTranscriptReader
    {
        readonly Queue<InteractiveTranscript> _remaining = new(snapshots);
        public InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started) =>
            _remaining.Count > 0 ? _remaining.Dequeue() : snapshots[^1];
        public string? FindPiSessionDirectory(string root, string sessionId) => null;
    }
}
