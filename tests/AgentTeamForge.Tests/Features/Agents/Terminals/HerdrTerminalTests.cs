using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;
using AgentTeamForge.Business.Features.Agents.Backends;
using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentTeamForge.Business.Features.Agents.Terminals;

namespace AgentTeamForge.Tests.Features.Agents.Terminals;

/// <summary>
/// Owned Linux Herdr launch (D7) against a scripted fake runner; the D2 vectors run against the
/// same production builders through <c>HerdrLaunchFixture</c>. One opt-in test drives the real binary.
/// </summary>
public class HerdrTerminalTests
{
    private const string Sentinel = "atf-credential-sentinel-d7";
    private const string Bootstrap = "/run/user/1000/atf/bootstrap-0001";

    private static readonly Dictionary<string, string?> Desktop = new()
    {
        ["PATH"] = "/usr/bin",
        ["HOME"] = "/home/u",
        ["WAYLAND_DISPLAY"] = "wayland-1",
        ["OPENAI_API_KEY"] = Sentinel,
        ["CLAUDE_CODE_MESSAGING_TOKEN"] = Sentinel,
        ["HERDR_SOCKET_PATH"] = "/home/u/.config/herdr/herdr.sock",
        ["HERDR_PANE_ID"] = Sentinel,
    };

    private static HerdrTerminal Terminal(FakeHerdr fake, IReadOnlyDictionary<string, string?>? env = null, Func<IReadOnlyDictionary<string, string>>? sessionEnvironment = null) =>
        new(new HerdrTerminalOptions
        {
            SessionEnvironment = sessionEnvironment,
            Environment = new Dictionary<string, string?>(env ?? Desktop)
            {
                ["HERDR_CONFIG_PATH"] = Path.Combine(Path.GetTempPath(), "atf-herdr-unit", "config.toml"),
            },
            SessionPrefix = "atf-test-",
            StartupTimeout = TimeSpan.FromMilliseconds(400),
            PollInterval = TimeSpan.FromMilliseconds(10),
            NewSessionName = fake.Preexisting ? () => FakeHerdr.TakenName : null,
        }, fake);

    [Fact]
    public void UnreadableLiveServerPidRemainsUnverified()
    {
        var session = new OwnedHerdrSession("atf-test", "/tmp/atf-test.sock", Environment.ProcessId, 11, "owner", "workspace");
        var binding = new HerdrTabBinding(session, "tab", "pane", "terminal", 0, 22);
        Assert.True(Terminal(new FakeHerdr()).HasUnverifiedLiveIdentity(binding));
    }

    [Fact]
    public async Task OwnedLaunch_RetainsHandleAndBootstrapProof()
    {
        using var state = new TempStateDir();
        var fake = new FakeHerdr();
        var environment = new Dictionary<string, string?>(Desktop) { ["HOME"] = state.Path };
        var terminal = Terminal(fake, environment);

        var session = await terminal.StartSessionAsync(CancellationToken.None);
        var binding = await terminal.OpenAgentTabAsync(session, "agent-a", "/work", Bootstrap, CancellationToken.None);

        Assert.StartsWith("atf-test-", session.SessionName, StringComparison.Ordinal);
        Assert.Equal(fake.ServerPid, session.ServerPid);
        Assert.Equal("w1", session.WorkspaceId);
        Assert.Equal(("w1:t2", "w1:p2", "term_a"), (binding.TabId, binding.PaneId, binding.TerminalId));
        Assert.Equal(fake.ShellPid, binding.ShellPid);
        Assert.Null(await terminal.VerifyBindingAsync(binding, CancellationToken.None));

        // D2 vectors against the production launch: exact argv, fixed server script, credential-free env.
        var server = Assert.Single(fake.Detached);
        Assert.Equal(OperatingSystem.IsMacOS() ? ["-c", HerdrCommands.MacServerScript, session.SessionName]
            : ["-f", "sh", "-c", HerdrCommands.ServerScript, session.SessionName], server.ArgumentList);
        var tab = fake.Calls.Single(c => c.Args is ["tab", "create", ..]);
        Assert.Equal(["tab", "create", "--workspace", "w1", "--cwd", "/work", "--label", "agent-a", "--env", "ATF_BOOTSTRAP_FILE=" + Bootstrap, "--no-focus"], tab.Args);
        Assert.Null(terminal.Env("CODEX_HOME"));
        foreach (var env in fake.Calls.Select(c => c.Env).Append(server.Environment))
        {
            Assert.DoesNotContain(env.Values, v => v?.Contains(Sentinel, StringComparison.Ordinal) == true);
            Assert.False(env.TryGetValue("HERDR_SOCKET_PATH", out var socket) && socket == "/home/u/.config/herdr/herdr.sock");
        }
        Assert.All(fake.Calls.Where(c => c.Args is ["workspace" or "tab" or "pane", ..]), c => Assert.Equal(fake.SocketPath, c.Env["HERDR_SOCKET_PATH"]));
    }

    [Fact]
    public async Task Recovery_rebinds_only_the_saved_server_pane_and_shell()
    {
        using var state = new TempStateDir();
        var fake = new FakeHerdr();
        var terminal = Terminal(fake, new Dictionary<string, string?>(Desktop) { ["HOME"] = state.Path });
        var session = await terminal.StartSessionAsync(CancellationToken.None);
        var binding = await terminal.OpenAgentTabAsync(session, "codex: review", "/work", Bootstrap,
            CancellationToken.None, onCreated: created => session = created);
        var saved = session with { ShellPid = binding.ShellPid, ShellStartTicks = binding.ShellStartTicks };

        Assert.NotNull(await terminal.RebindAsync(saved, Bootstrap, CancellationToken.None));
        Assert.Null(await terminal.RebindAsync(saved, "/wrong-bootstrap", CancellationToken.None));
        fake.Replace(Replacement.ShellReplaced);
        Assert.Null(await terminal.RebindAsync(saved, Bootstrap, CancellationToken.None));
    }

    [Fact]
    public async Task Restart_reattaches_a_submitted_job_to_its_saved_live_pane()
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var fake = new FakeHerdr { BootstrapFromTab = true };
        var terminal = Terminal(fake);
        var job = f.Submit("live");
        var claim = f.Store.BeginNextAttempt()!;
        f.Store.RecordStartup(new RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation), "submitted");
        var bootstrap = Path.Combine(state.Path, "herdr", "atftest.bootstrap");
        Directory.CreateDirectory(Path.GetDirectoryName(bootstrap)!);
        File.WriteAllText(bootstrap, "atftest");
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null, bootstrap)
        { JobId = job.JobId };
        var session = await terminal.StartSessionAsync(CancellationToken.None);
        var binding = await terminal.OpenAgentTabAsync(session, "codex: live", state.Path, bootstrap,
            CancellationToken.None, onCreated: created => session = created);
        HerdrOwnedSessions.Save(launch, session with { ShellPid = binding.ShellPid, ShellStartTicks = binding.ShellStartTicks });

        var backend = new HerdrInteractiveBackend(terminal, InteractiveAgentKind.Codex, state.Path);
        var reattached = backend.Reattach(f.Store.GetJob(job.JobId)!, f.Store.GetRuns(job.JobId).Single(), out var gone);

        Assert.False(gone);
        Assert.NotNull(reattached);
        await reattached.DisposeAsync();
    }

    /// <summary>A completed Claude parent whose idle pane (and native transcript) outlived the daemon.</summary>
    private static async Task<(HerdrInteractiveBackend Backend, BackendCatalog Catalog, AcceptJob Accept, JobRecord Parent, string Transcript)>
        CompletedParentWithLivePane(JobFixture f, TempStateDir state, FakeHerdr fake, Func<IReadOnlySet<string>?>? liveSessions = null, string parentTurn = ParentTurnDone,
            Action<InteractiveLaunch>? probed = null)
    {
        var terminal = Terminal(fake, new Dictionary<string, string?>(Desktop) { ["HOME"] = state.Path });
        // The /proc probe of the pane's live native session, which the fake pane cannot provide.
        var backend = new HerdrInteractiveBackend(terminal, InteractiveAgentKind.Claude, state.Path)
        {
            LiveSessionProbe = (launch, _) =>
            {
                probed?.Invoke(launch);
                return liveSessions is null ? new HashSet<string> { "claude-native" } : liveSessions();
            },
        };
        var catalog = new BackendCatalog().Register(BackendCatalog.Claude, () => backend);
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, f.TestProfile, f.Admission, catalog.Names);
        var parent = accept.Execute(new SubmitJobRequest("parent", "work", null, false) { Backend = BackendCatalog.Claude }).Job!;
        var parentClaim = f.Store.BeginNextAttempt()!;
        var parentRun = new RunRef(parent.JobId, parentClaim.RunId, parentClaim.Generation, parentClaim.Correlation);
        Assert.True(f.Store.RecordSession(parentRun, "claude-native"));
        Assert.True(f.Store.Complete(parentRun, "ready"));
        // The parent's pane stays open; only the parent owns its record.
        var bootstrap = Path.Combine(state.Path, "herdr", "atftest.bootstrap");
        Directory.CreateDirectory(Path.GetDirectoryName(bootstrap)!);
        File.WriteAllText(bootstrap, "atftest");
        var session = await terminal.StartSessionAsync(CancellationToken.None);
        var binding = await terminal.OpenAgentTabAsync(session, "claude: parent", state.Path, bootstrap,
            CancellationToken.None, onCreated: created => session = created);
        HerdrOwnedSessions.Save(new InteractiveLaunch(InteractiveAgentKind.Claude, "atftest", state.Path, null, null, bootstrap)
        { JobId = parent.JobId }, session with { ShellPid = binding.ShellPid, ShellStartTicks = binding.ShellStartTicks });
        var transcript = Path.Combine(Directory.CreateDirectory(Path.Combine(state.Path, ".claude", "projects", "scratch")).FullName, "claude-native.jsonl");
        File.WriteAllLines(transcript,
        [
            """{"type":"user","isSidechain":false,"sessionId":"claude-native","message":{"role":"user","content":"work atf-corr:""" + parentClaim.Correlation + "\"}}",
            .. parentTurn.Split('\n'),
        ]);
        return (backend, catalog, accept, f.Store.GetJob(parent.JobId)!, transcript);
    }

    private const string ParentTurnDone =
        """{"type":"assistant","isSidechain":false,"sessionId":"claude-native","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"ready"}]}}""";

    private static void RecoverRecords(JobFixture f, TempStateDir state) =>
        new AgentTeamForge.Business.Features.Recovery.RecoverOnStartup(f.Store,
            () => HerdrOwnedSessions.Recover(state.Path, f.Store.FenceSession, _ => { })).Execute();

    [Fact]
    public async Task Recovered_pane_runs_in_the_jobs_subdirectory_of_its_worktree()
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var repo = Directory.CreateDirectory(state.File("repo/sub")).Parent!.FullName;
        using (var git = Process.Start(new ProcessStartInfo("git", ["init", "-q", repo]))!) { await git.WaitForExitAsync(TestContext.Current.CancellationToken); }
        var worktree = Directory.CreateDirectory(state.File("worktree/sub")).Parent!.FullName;
        InteractiveLaunch? rebound = null;
        var (backend, _, _, parent, _) = await CompletedParentWithLivePane(f, state, new FakeHerdr { BootstrapFromTab = true }, probed: launch => rebound = launch);
        // Submitted from repo/sub with a worktree: the agent ran in worktree/sub, so its rebound pane does too.
        var owner = parent with { Cwd = Path.Combine(repo, "sub"), WorktreePath = worktree };

        backend.RecoverTerminalOwner(owner, [], () => true);

        Assert.Equal(Path.Combine(worktree, "sub"), Path.TrimEndingDirectorySeparator(rebound?.WorkingDirectory ?? ""));
    }

    [Fact]
    public async Task Restart_reattaches_a_native_follow_up_to_its_parents_live_pane_and_settles_it()
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var fake = new FakeHerdr { BootstrapFromTab = true };
        var (backend, catalog, accept, parent, transcript) = await CompletedParentWithLivePane(f, state, fake);
        var child = new FollowUpJob(f.Store, JobFixture.Operator, accept).Execute(new FollowUpRequest(parent.JobId, "next", "next")).Job!;
        var claim = f.Store.BeginNativeClaudeAttempt(parent.JobId, Path.Combine(state.Path, ".claude"), _ => true)!;
        Assert.Equal(child.JobId, claim.Job.JobId);
        f.Store.RecordNativeClaudePost(child.JobId, claim.Correlation);
        f.Store.RecordNativeClaudeReceipt(child.JobId, claim.Correlation);
        RecoverRecords(f, state);
        Assert.Equal(JobStatus.NeedsReconciliation, f.Store.GetJob(child.JobId)!.Status);

        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        dispatcher.RestoreAfterRestart(f.Store.RestartCandidates());
        using var lifetime = new CancellationTokenSource();
        var loop = dispatcher.RunAsync(lifetime.Token);
        try
        {
            Assert.Equal((JobStatus.Running, null), (f.Store.GetJob(child.JobId)!.Status, f.Store.GetJob(child.JobId)!.ReasonCode));
            Assert.False(f.Store.IsSessionFenced(child.JobId)); // Nor the parent, a session peer.
            Assert.Equal(JobStatus.Completed, f.Store.GetJob(parent.JobId)!.Status);
            Assert.DoesNotContain(fake.Calls, c => c.Args is ["pane" or "tab", "close", ..]);

            File.AppendAllLines(transcript,
            [
                """{"type":"user","isMeta":true,"isSidechain":false,"sessionId":"claude-native","message":{"role":"user","content":"Another Claude session sent a message:\nnext atf-corr:""" + claim.Correlation + "\"}}",
                """{"type":"assistant","isSidechain":false,"sessionId":"claude-native","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"answered"}]}}""",
            ]);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (f.Store.GetJob(child.JobId)!.Status == JobStatus.Running && DateTime.UtcNow < deadline) { await Task.Delay(50, TestContext.Current.CancellationToken); }
            Assert.Equal((JobStatus.Completed, "answered"), (f.Store.GetJob(child.JobId)!.Status, f.Store.GetJob(child.JobId)!.ResultText));
            // The pane is retained, bound to its exact native session, for the next native turn.
            Assert.True(backend.HasIdleClaudeSession("claude-native"));
            Assert.True(backend.HasIdleSession("claude-native"));
        }
        finally
        {
            lifetime.Cancel();
            await loop;
        }
    }

    [Fact]
    public async Task Restart_settles_a_native_turn_from_its_transcript_and_releases_the_parents_fence()
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var fake = new FakeHerdr { BootstrapFromTab = true };
        var (backend, catalog, accept, parent, transcript) = await CompletedParentWithLivePane(f, state, fake);
        var child = new FollowUpJob(f.Store, JobFixture.Operator, accept).Execute(new FollowUpRequest(parent.JobId, "next", "next")).Job!;
        var claim = f.Store.BeginNativeClaudeAttempt(parent.JobId, Path.Combine(state.Path, ".claude"), _ => true)!;
        f.Store.RecordNativeClaudePost(child.JobId, claim.Correlation);
        // The native turn finished while the daemon was down.
        File.AppendAllLines(transcript,
        [
            """{"type":"user","isMeta":true,"isSidechain":false,"sessionId":"claude-native","message":{"role":"user","content":"Another Claude session sent a message:\nnext atf-corr:""" + claim.Correlation + "\"}}",
            """{"type":"assistant","isSidechain":false,"sessionId":"claude-native","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"answered"}]}}""",
        ]);
        RecoverRecords(f, state);
        Assert.True(f.Store.IsSessionFenced(parent.JobId));

        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        dispatcher.RestoreAfterRestart(f.Store.RestartCandidates());
        using var lifetime = new CancellationTokenSource();
        var loop = dispatcher.RunAsync(lifetime.Token);
        lifetime.Cancel();
        await loop;

        Assert.Equal((JobStatus.Completed, "answered"), (f.Store.GetJob(child.JobId)!.Status, f.Store.GetJob(child.JobId)!.ResultText));
        Assert.False(f.Store.IsSessionFenced(parent.JobId));
        Assert.True(backend.HasIdleClaudeSession("claude-native"));
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["pane" or "tab", "close", ..]);
    }

    [Fact]
    public async Task Restart_retains_a_completed_idle_pane_so_a_follow_up_is_accepted_and_delivered_there()
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var fake = new FakeHerdr { BootstrapFromTab = true };
        var (_, catalog, accept, parent, _) = await CompletedParentWithLivePane(f, state, fake);
        RecoverRecords(f, state);
        var follow = new FollowUpJob(f.Store, JobFixture.Operator, accept);
        Assert.Equal(JobErrors.ParentNotReady, follow.Execute(new FollowUpRequest(parent.JobId, "early", "early")).Error);

        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        dispatcher.RestoreAfterRestart(f.Store.RestartCandidates());
        using var lifetime = new CancellationTokenSource();
        var loop = dispatcher.RunAsync(lifetime.Token);
        try
        {
            Assert.False(f.Store.IsSessionFenced(parent.JobId));
            var child = follow.Execute(new FollowUpRequest(parent.JobId, "next turn", "next")).Job!;
            dispatcher.Signal();
            await Bounded.Until(() => fake.Snapshot().Any(c => c.Args is ["--session", _, "agent", "prompt", _, var prompt, ..]
                && prompt.StartsWith("next turn", StringComparison.Ordinal)), "follow-up prompt delivery");
            Assert.Equal(JobStatus.Running, f.Store.GetJob(child.JobId)!.Status);
            // Delivered into the retained pane: no new tab, nothing closed.
            Assert.Single(fake.Snapshot(), c => c.Args is ["tab", "create", ..]);
            Assert.DoesNotContain(fake.Snapshot(), c => c.Args is ["pane" or "tab", "close", ..]);
        }
        finally
        {
            lifetime.Cancel();
            await loop;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_keeps_a_busy_owner_pane_fenced_and_releases_a_proven_gone_one_untouched(bool gone)
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var fake = new FakeHerdr { BootstrapFromTab = true };
        var (backend, catalog, _, parent, _) = await CompletedParentWithLivePane(f, state, fake);
        RecoverRecords(f, state);
        if (gone) { fake.Replace(Replacement.ServerRestarted); }
        else { for (var i = 0; i < 8; i++) { fake.AgentStatuses.Enqueue("working"); } } // A human typed into the pane.

        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        dispatcher.RestoreAfterRestart(f.Store.RestartCandidates());
        using var lifetime = new CancellationTokenSource();
        var loop = dispatcher.RunAsync(lifetime.Token);
        lifetime.Cancel();
        await loop;

        Assert.Equal(!gone, f.Store.IsSessionFenced(parent.JobId));
        Assert.False(backend.HasIdleSession("claude-native"));
        Assert.Equal(JobStatus.Completed, f.Store.GetJob(parent.JobId)!.Status);
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["pane" or "tab", "close", ..] or ["session", "stop" or "delete", ..]);
        Assert.Single(fake.Calls, c => c.Args is ["tab", "create", ..]);
    }

    private static async Task RestartDispatcherAsync(JobFixture f, BackendCatalog catalog)
    {
        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        dispatcher.RestoreAfterRestart(f.Store.RestartCandidates());
        using var lifetime = new CancellationTokenSource();
        var loop = dispatcher.RunAsync(lifetime.Token);
        lifetime.Cancel();
        await loop;
    }

    [Fact]
    public async Task Stop_agent_racing_restart_recovery_keeps_the_pane_fenced()
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var fake = new FakeHerdr { BootstrapFromTab = true };
        var (backend, catalog, _, parent, _) = await CompletedParentWithLivePane(f, state, fake);
        RecoverRecords(f, state);
        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        dispatcher.RestoreAfterRestart(f.Store.RestartCandidates());
        using var lifetime = new CancellationTokenSource();

        // stop_agent holds the gate across its fence and pane close; this close does not finish.
        Task loop;
        backend.SessionStopGate.Enter();
        try
        {
            loop = Task.Run(() => dispatcher.RunAsync(lifetime.Token), TestContext.Current.CancellationToken);
            Thread.Sleep(2000); // Longer than recovery takes. The gate is thread-affine: no await while holding it.
            Assert.True(f.Store.IsSessionFenced(parent.JobId)); // Recovery waits for the gate.
            Assert.True(f.Store.TryFenceSessionForStop(parent.JobId));
        }
        finally { backend.SessionStopGate.Exit(); }
        lifetime.Cancel();
        await loop;

        Assert.True(f.Store.IsSessionFenced(parent.JobId));
        Assert.False(backend.HasIdleSession("claude-native"));
    }

    [Theory]
    [InlineData("human")]
    [InlineData("background")]
    [InlineData("unfinished")]
    [InlineData("malformed")]
    public async Task Restart_keeps_an_idle_pane_fenced_unless_its_latest_turn_settled(string latest)
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var fake = new FakeHerdr { BootstrapFromTab = true };
        var turn = latest switch
        {
            // A human typed into the retained pane while the daemon was down.
            "human" => ParentTurnDone + "\n" + """{"type":"user","isSidechain":false,"sessionId":"claude-native","message":{"role":"user","content":"one more thing"}}""",
            // Idle while a background task the turn started is still running.
            "background" => """{"type":"assistant","isSidechain":false,"sessionId":"claude-native","message":{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"bg1","name":"Bash","input":{"command":"make","run_in_background":true}}]}}"""
                + "\n" + ParentTurnDone,
            // A partially flushed line can hide the next human turn.
            "malformed" => ParentTurnDone + "\n" + """{"type":"user","isSidechain":fa""" + "\n"
                + """{"type":"user","isSidechain":false,"sessionId":"claude-native","message":{"role":"user","content":"later"}}""",
            _ => """{"type":"assistant","isSidechain":false,"sessionId":"claude-native","message":{"role":"assistant","content":[{"type":"text","text":"working on it"}]}}""",
        };
        var (backend, catalog, _, parent, _) = await CompletedParentWithLivePane(f, state, fake, parentTurn: turn);
        RecoverRecords(f, state);

        await RestartDispatcherAsync(f, catalog);

        Assert.True(f.Store.IsSessionFenced(parent.JobId));
        Assert.False(backend.HasIdleSession("claude-native"));
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["pane" or "tab", "close", ..]);
    }

    [Theory]
    [InlineData("other")]
    [InlineData("claude-native,other")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Restart_keeps_a_pane_fenced_unless_it_runs_exactly_the_owners_session(string? live)
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var fake = new FakeHerdr { BootstrapFromTab = true };
        var (backend, catalog, _, parent, _) = await CompletedParentWithLivePane(f, state, fake,
            () => live?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet());
        RecoverRecords(f, state);

        await RestartDispatcherAsync(f, catalog);

        Assert.True(f.Store.IsSessionFenced(parent.JobId));
        Assert.False(backend.HasIdleSession("claude-native"));
        Assert.False(backend.HasIdleClaudeSession("claude-native"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Restart_of_a_busy_parent_and_its_native_turn_shares_one_pane_in_either_settlement_order(bool nativeSettlesFirst)
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var fake = new FakeHerdr { BootstrapFromTab = true };
        var terminal = Terminal(fake, new Dictionary<string, string?>(Desktop) { ["HOME"] = state.Path });
        var backend = new HerdrInteractiveBackend(terminal, InteractiveAgentKind.Codex, state.Path)
        { LiveSessionProbe = (_, _) => new HashSet<string> { "thread-busy" } };
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, f.TestProfile, f.Admission,
            new BackendCatalog().Register(BackendCatalog.Codex, () => backend).Names);
        var parent = accept.Execute(new SubmitJobRequest("parent", "work", null, false) { Backend = BackendCatalog.Codex }).Job!;
        var claim = f.Store.BeginNextAttempt()!;
        var run = new RunRef(parent.JobId, claim.RunId, claim.Generation, claim.Correlation);
        f.Store.RecordStartup(run, "submitted");
        Assert.True(f.Store.RecordSession(run, "thread-busy"));
        var bootstrap = Path.Combine(state.Path, "herdr", "atftest.bootstrap");
        Directory.CreateDirectory(Path.GetDirectoryName(bootstrap)!);
        File.WriteAllText(bootstrap, "atftest");
        var session = await terminal.StartSessionAsync(CancellationToken.None);
        var binding = await terminal.OpenAgentTabAsync(session, "codex: parent", state.Path, bootstrap,
            CancellationToken.None, onCreated: created => session = created);
        HerdrOwnedSessions.Save(new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null, bootstrap)
        { JobId = parent.JobId }, session with { ShellPid = binding.ShellPid, ShellStartTicks = binding.ShellStartTicks });
        var rollout = Directory.CreateDirectory(Path.Combine(state.Path, ".codex", "sessions")).FullName;
        File.WriteAllLines(Path.Combine(rollout, "rollout-test-thread-busy.jsonl"),
            ["""{"type":"session_meta","payload":{"id":"thread-busy","source":"cli"}}"""]);
        var running = f.Store.GetJob(parent.JobId)!;

        // Restart: the parent's own observer and its queued native turn rebind the same pane.
        var observer = backend.Reattach(running, f.Store.GetRuns(parent.JobId).Single(), out var gone);
        Assert.False(gone);
        Assert.NotNull(observer);
        Assert.Equal(parent.JobId, backend.ReattachNativeTurn("thread-busy", [running], out _));
        Assert.True(backend.HasLiveCodexSession("thread-busy"));

        if (nativeSettlesFirst) { backend.RememberNativeTurn("thread-busy"); await observer.DisposeAsync(); }
        else { await observer.DisposeAsync(); backend.RememberNativeTurn("thread-busy"); }

        Assert.DoesNotContain(fake.Calls, c => c.Args is ["pane" or "tab", "close", ..]);
        Assert.True(backend.HasIdleSession("thread-busy"));
        Assert.True(backend.HasIdleCodexSession("thread-busy"));
    }

    [Fact]
    public async Task Restart_keeps_unrebindable_live_pane_fenced_and_fails_only_a_proven_gone_one()
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var fake = new FakeHerdr();
        var terminal = Terminal(fake);
        var owned = await terminal.StartSessionAsync(CancellationToken.None);
        var job = f.Submit("live");
        var claim = f.Store.BeginNextAttempt()!;
        f.Store.RecordStartup(new RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation), "submitted");
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null,
            Path.Combine(state.Path, "herdr", "atftest.bootstrap"))
        { JobId = job.JobId };
        Directory.CreateDirectory(Path.GetDirectoryName(launch.BootstrapPath)!);
        File.WriteAllText(launch.BootstrapPath, "");
        // A record from a daemon that predates saved shell identity: live, but not rebindable.
        HerdrOwnedSessions.Save(launch, owned);
        new AgentTeamForge.Business.Features.Recovery.RecoverOnStartup(f.Store).Execute();
        var backend = new HerdrInteractiveBackend(terminal, InteractiveAgentKind.Codex, state.Path);

        async Task RestartAsync()
        {
            using var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
            dispatcher.RestoreAfterRestart(f.Store.RestartCandidates());
            using var lifetime = new CancellationTokenSource();
            var loop = dispatcher.RunAsync(lifetime.Token);
            lifetime.Cancel();
            await loop;
        }

        await RestartAsync();
        Assert.Equal((JobStatus.NeedsReconciliation, "daemon_restart_uncertain"),
            (f.Store.GetJob(job.JobId)!.Status, f.Store.GetJob(job.JobId)!.ReasonCode));
        Assert.True(f.Store.IsSessionFenced(job.JobId));

        fake.Replace(Replacement.ServerRestarted);
        await RestartAsync();
        Assert.Equal((JobStatus.Failed, "daemon_restart_agent_gone"),
            (f.Store.GetJob(job.JobId)!.Status, f.Store.GetJob(job.JobId)!.ReasonCode));
        Assert.False(f.Store.IsSessionFenced(job.JobId));
    }

    [Fact]
    public async Task ExistingDefaultCodexHomeIsPinnedForSharedTab()
    {
        using var state = new TempStateDir();
        Directory.CreateDirectory(Path.Combine(state.Path, ".codex"));
        var fake = new FakeHerdr { SharedRunning = true };
        var environment = new Dictionary<string, string?>(Desktop) { ["HOME"] = state.Path };
        var terminal = Terminal(fake, environment);

        var session = await terminal.ExistingSessionAsync("default", CancellationToken.None);
        await terminal.OpenAgentTabAsync(session, "agent-a", "/work", Bootstrap, CancellationToken.None);

        Assert.Equal(Path.Combine(state.Path, ".codex"), terminal.Env("CODEX_HOME"));
        Assert.Contains("CODEX_HOME=" + terminal.Env("CODEX_HOME"), fake.Calls.Single(c => c.Args is ["tab", "create", ..]).Args);
    }

    [Theory]
    [InlineData(SpawnFault.SetsidFails)]
    [InlineData(SpawnFault.ServerNeverRuns)]
    [InlineData(SpawnFault.ForeignServerProcess)]
    [InlineData(SpawnFault.WorkspaceCreateFails)]
    public async Task SpawnFailure_NoForeignCleanup(SpawnFault fault)
    {
        var fake = new FakeHerdr { Fault = fault };

        await Assert.ThrowsAsync<HerdrLaunchException>(() => Terminal(fake).StartSessionAsync(CancellationToken.None));

        Assert.DoesNotContain(fake.Calls, c => c.Args is ["session", "stop" or "delete", ..] or ["tab" or "pane" or "workspace", "close", ..] or ["server", "stop"]);
        Assert.Empty(fake.Killed);
    }

    [Fact]
    public async Task ExistingSession_RefusesReuseWithoutTouchingIt()
    {
        var fake = new FakeHerdr { Preexisting = true };

        await Assert.ThrowsAsync<HerdrLaunchException>(() => Terminal(fake).StartSessionAsync(CancellationToken.None));

        Assert.Empty(fake.Detached);
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["session", "stop" or "delete", ..]);
    }

    [Fact]
    public async Task SharedPlacement_ClosesOnlyRecordedTab()
    {
        var fake = new FakeHerdr { SharedRunning = true };
        var terminal = Terminal(fake);
        var session = await terminal.ExistingSessionAsync("default", CancellationToken.None);
        Assert.True(session.Shared);
        Assert.Empty(fake.Detached);
        var binding = await terminal.OpenAgentTabAsync(session, "agent-a", "/work", Bootstrap, CancellationToken.None,
            onCreated: created => session = created);
        Assert.Equal("w1:t2", binding.TabId);
        await terminal.StopOwnedSessionAsync(session, CancellationToken.None);
        Assert.Single(fake.Calls, c => c.Args is ["pane", "close", "w1:p2"]);
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["session", "stop" or "delete", ..] or ["workspace" or "tab", "close", ..]);

        fake.TabReplaced = true;
        await Assert.ThrowsAsync<HerdrLaunchException>(() => terminal.StopOwnedSessionAsync(session, CancellationToken.None));
        Assert.Single(fake.Calls, c => c.Args is ["pane", "close", "w1:p2"]);

        // After a Herdr restart the recorded tab died with its server: stop succeeds and touches nothing.
        fake.TabReplaced = false;
        fake.Replace(Replacement.ServerRestarted);
        var calls = fake.Calls.Count;
        await terminal.StopOwnedSessionAsync(session, CancellationToken.None);
        Assert.Equal(calls, fake.Calls.Count);
    }

    [Fact]
    public async Task SharedSession_StartsAStoppedListedSessionWithoutOwningIt()
    {
        var fake = new FakeHerdr { DefaultRunning = false };

        var session = await Terminal(fake).SharedSessionAsync("default", CancellationToken.None);

        Assert.True(session.Shared);
        var server = Assert.Single(fake.Detached);
        Assert.Equal("default", server.ArgumentList[^1]);
        Assert.Equal("/home/u", server.Environment["HERDR_STARTUP_CWD"]);
        Assert.DoesNotContain("HERDR_SOCKET_PATH", server.Environment.Keys);
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["workspace" or "tab", "create", ..] or ["session", "stop" or "delete", ..]);
    }

    [Fact]
    public async Task SharedSession_StoppedSessionGetsTheAgentTab()
    {
        using var state = new TempStateDir();
        var fake = new FakeHerdr { DefaultRunning = false, BootstrapFromTab = true, SharedWorkspaceLabel = Path.GetFileName(state.Path) };
        var launch = new InteractiveLaunch(InteractiveAgentKind.Claude, "atftest", state.Path, null, null, state.File("herdr/bootstrap"))
        { JobId = "job-stopped", HerdrPlacement = "herdr-session:default" };

        await new HerdrAgentControl(Terminal(fake)).StartAsync(launch, CancellationToken.None);

        Assert.Single(fake.Detached);
        Assert.Single(fake.Calls, c => c.Args is ["tab", "create", ..]);
        Assert.Contains(fake.Calls, c => c.Args is ["agent", "start", "atftest", ..]);
    }

    [Fact]
    public async Task AgentStart_RetriesAPaneHerdrStillReadsAsBusy()
    {
        using var state = new TempStateDir();
        var fake = new FakeHerdr { SharedRunning = true, BootstrapFromTab = true, AgentStartBusy = 2, SharedWorkspaceLabel = Path.GetFileName(state.Path) };
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null, state.File("herdr/bootstrap"))
        { JobId = "job-busy", HerdrPlacement = "herdr-session:default" };

        await new HerdrAgentControl(Terminal(fake)).StartAsync(launch, CancellationToken.None);

        Assert.Equal(3, fake.Calls.Count(c => c.Args is ["agent", "start", "atftest", ..]));
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["pane" or "tab", "close", ..]);
    }

    [Fact]
    public async Task ResumeLaunch_UsesLongerStartTimeoutAndSurvivesASlowStartWhilePaneLives()
    {
        using var state = new TempStateDir();
        var fake = new FakeHerdr { SharedRunning = true, BootstrapFromTab = true, AgentStartFails = true, SharedWorkspaceLabel = Path.GetFileName(state.Path) };
        var launch = new InteractiveLaunch(InteractiveAgentKind.Claude, "atftest", state.Path, "native-1", null, state.File("herdr/bootstrap"))
        { JobId = "job-resume", HerdrPlacement = "herdr-session:default" };

        await new HerdrAgentControl(Terminal(fake)).StartAsync(launch, CancellationToken.None);

        var start = Assert.Single(fake.Calls, c => c.Args is ["agent", "start", "atftest", ..]);
        Assert.Equal(HerdrAgentControl.ResumeStartTimeoutMs.ToString(), start.Args[Array.IndexOf(start.Args, "--timeout") + 1]);
    }

    [Fact]
    public async Task SharedSession_ConcurrentSubmitsStartOneServer()
    {
        var fake = new FakeHerdr { DefaultRunning = false };
        var terminal = Terminal(fake);

        await Task.WhenAll(terminal.SharedSessionAsync("default", CancellationToken.None), terminal.SharedSessionAsync("default", CancellationToken.None));

        Assert.Single(fake.Detached);
    }

    [Fact]
    public async Task SharedSession_SubmitDuringDelayedServerReadinessWaitsForTheStart()
    {
        var fake = new FakeHerdr { DefaultRunning = false, DefaultReadyAfterListCalls = 3 };
        var terminal = Terminal(fake);

        var first = terminal.SharedSessionAsync("default", CancellationToken.None);
        var second = terminal.SharedSessionAsync("default", CancellationToken.None);
        var sessions = await Task.WhenAll(first, second);

        Assert.Single(fake.Detached);
        Assert.Equal(sessions[0].ServerPid, sessions[1].ServerPid);
        Assert.All(sessions, session => Assert.True(session.Shared));
        Assert.Null(terminal.CheckSharedSession("default"));
    }

    [Fact]
    public async Task SharedSession_AmbiguousAbsentOrHalfAliveNeverStarts()
    {
        var ambiguous = new FakeHerdr { DefaultRunning = false, DefaultServerCount = 2 };
        var error = await Assert.ThrowsAsync<HerdrLaunchException>(() => Terminal(ambiguous).SharedSessionAsync("default", CancellationToken.None));
        Assert.Contains("2 servers", error.Message);
        Assert.Contains("2 servers", Terminal(ambiguous).CheckSharedSession("default"));

        var absent = new FakeHerdr();
        await Assert.ThrowsAsync<HerdrLaunchException>(() => Terminal(absent).SharedSessionAsync("typo", CancellationToken.None));
        Assert.NotNull(Terminal(absent).CheckSharedSession("typo"));

        var halfAlive = new FakeHerdr { DefaultRunning = false, SharedRunning = true };
        await Assert.ThrowsAsync<HerdrLaunchException>(() => Terminal(halfAlive).SharedSessionAsync("default", CancellationToken.None));

        Assert.All([ambiguous, absent, halfAlive], f => Assert.Empty(f.Detached));
    }

    [Fact]
    public void CheckSharedSession_AcceptsStoppedWithoutStartingIt()
    {
        var fake = new FakeHerdr { DefaultRunning = false };

        Assert.Null(Terminal(fake).CheckSharedSession("default"));
        Assert.Null(Terminal(new FakeHerdr { SharedRunning = true }).CheckSharedSession("default"));
        Assert.Empty(fake.Detached);
    }

    [Fact]
    public async Task RestoreSweep_NeverStartsAStoppedSession()
    {
        var fake = new FakeHerdr { DefaultRunning = false };
        var stale = new OwnedHerdrSession("default", "/s", 999, 1, "", "")
        { Shared = true, JobId = "job-r", AgentName = RestoredAgent, PaneId = "w1:p2", TabId = "w1:t2" };

        Assert.Equal(0, await Terminal(fake).CloseRestoredPanesAsync("default", [stale], CancellationToken.None));

        Assert.Empty(fake.Detached);
    }

    const string RestoredAgent = "atf0123456789abcdef0123";

    static async Task<(OwnedHerdrSession Session, InteractiveLaunch Launch)> SavedSharedRecord(HerdrTerminal terminal, string state, string agentName = RestoredAgent)
    {
        var session = await terminal.ExistingSessionAsync("default", CancellationToken.None);
        session = (await terminal.OpenAgentTabAsync(session, "agent-a", "/work", Bootstrap, CancellationToken.None, onCreated: created => session = created)) is var b
            ? session with { TabId = b.TabId, PaneId = b.PaneId, TerminalId = b.TerminalId } : session;
        var launch = new InteractiveLaunch(InteractiveAgentKind.Claude, agentName, state, null, null, Path.Combine(state, "herdr", agentName + ".bootstrap")) { JobId = "job-r" };
        Directory.CreateDirectory(Path.GetDirectoryName(launch.BootstrapPath)!);
        HerdrOwnedSessions.Save(launch, session);
        return (session, launch);
    }

    static int Sweep(HerdrTerminal terminal, string state) =>
        HerdrOwnedSessions.SweepRestored(state, (name, records) => terminal.CloseRestoredPanesAsync(name, records, CancellationToken.None).GetAwaiter().GetResult(), _ => { });

    [Fact]
    public async Task Restored_pane_with_recorded_agent_name_is_closed()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr { SharedRunning = true };
        var terminal = Terminal(fake);
        var (_, launch) = await SavedSharedRecord(terminal, state.Path);
        fake.Replace(Replacement.ServerRestarted);
        fake.ListedAgentName = RestoredAgent;
        Assert.Equal(1, Sweep(terminal, state.Path));
        Assert.Single(fake.Calls, c => c.Args is ["pane", "close", "w1:p2"]);
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["tab", "close", ..] or ["session", "stop", ..]);
        Assert.True(File.Exists(HerdrOwnedSessions.PathFor(launch)));
    }

    [Theory]
    [InlineData("otherName", "w1:p2", "w1:t2", true, true)]
    [InlineData(RestoredAgent, "w1:p7", "w1:t2", true, true)]
    [InlineData(RestoredAgent, "w1:p2", "w1:t7", true, true)]
    [InlineData(RestoredAgent, "w1:p2", "w1:t2", false, true)]
    [InlineData(RestoredAgent, "w1:p2", "w1:t2", true, false)]
    public async Task Restored_sweep_never_closes_unproven_panes(string name, string pane, string tab, bool restarted, bool sessionRunning)
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var setup = new FakeHerdr { SharedRunning = true };
        var (session, launch) = await SavedSharedRecord(Terminal(setup), state.Path);
        var fake = new FakeHerdr { SharedRunning = true, DefaultRunning = sessionRunning, ListedAgentName = name, ListedAgentPane = pane, ListedAgentTab = tab };
        if (restarted) { fake.Replace(Replacement.ServerRestarted); }
        // Recorded server identity as seen by this fake: alive unless restarted.
        HerdrOwnedSessions.Save(launch, session with { ServerPid = fake.ServerPid, ServerStartTicks = 77 });
        Assert.Equal(0, Sweep(Terminal(fake), state.Path));
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["pane", "close", ..]);
        if (!restarted) { Assert.Empty(fake.Calls); }
    }

    [Fact]
    public async Task Legacy_record_derives_agent_name_from_file_name()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr { SharedRunning = true };
        var terminal = Terminal(fake);
        var (session, _) = await SavedSharedRecord(terminal, state.Path);
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(state.Path, "herdr", RestoredAgent + ".owned.json")))!.AsObject();
        legacy.Remove("AgentName");
        var dir = Path.Combine(state.Path, "herdr");
        File.Delete(Path.Combine(dir, RestoredAgent + ".owned.json"));
        File.WriteAllText(Path.Combine(dir, RestoredAgent + ".owned.json"), legacy.ToJsonString());
        File.WriteAllText(Path.Combine(dir, "bootstrap.owned.json"), legacy.ToJsonString());
        fake.Replace(Replacement.ServerRestarted);
        fake.ListedAgentName = RestoredAgent;
        Assert.Equal(1, Sweep(terminal, state.Path));
        Assert.Single(fake.Calls, c => c.Args is ["pane", "close", "w1:p2"]);
        fake.ListedAgentName = "bootstrap";
        Assert.Equal(0, Sweep(terminal, state.Path));
        Assert.Single(fake.Calls, c => c.Args is ["pane", "close", ..]);
    }

    [Theory]
    [InlineData("claude", "claude")]
    [InlineData("bootstrap", RestoredAgent)]
    [InlineData("atfaaaaaaaaaaaaaaaaaaaa", RestoredAgent)]
    public async Task Sweep_refuses_record_whose_name_is_not_its_atf_file_name(string fileName, string savedName)
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr { SharedRunning = true };
        var terminal = Terminal(fake);
        var (session, launch) = await SavedSharedRecord(terminal, state.Path);
        File.Delete(HerdrOwnedSessions.PathFor(launch));
        var other = launch with
        {
            AgentName = savedName,
            BootstrapPath = Path.Combine(state.Path, "herdr", fileName + ".bootstrap")
        };
        HerdrOwnedSessions.Save(other, session);
        fake.Replace(Replacement.ServerRestarted);
        fake.ListedAgentName = savedName;

        Assert.Equal(0, Sweep(terminal, state.Path));
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["pane", "close", ..]);
    }

    [Fact]
    public async Task Close_restored_refuses_record_from_another_session()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr { SharedRunning = true };
        var terminal = Terminal(fake);
        var (session, _) = await SavedSharedRecord(terminal, state.Path);
        fake.Replace(Replacement.ServerRestarted);
        fake.ListedAgentName = RestoredAgent;

        Assert.Equal(0, await terminal.CloseRestoredPanesAsync("default",
            [session with { SessionName = "other", AgentName = RestoredAgent }], CancellationToken.None));
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["pane", "close", ..]);
    }

    [Fact]
    public async Task Close_restored_refuses_non_atf_recorded_agent_name()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr { SharedRunning = true };
        var terminal = Terminal(fake);
        var (session, _) = await SavedSharedRecord(terminal, state.Path);
        fake.Replace(Replacement.ServerRestarted);
        fake.ListedAgentName = "claude";

        Assert.Equal(0, await terminal.CloseRestoredPanesAsync("default",
            [session with { AgentName = "claude" }], CancellationToken.None));
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["pane", "close", ..]);
    }

    [Fact]
    public async Task Stop_after_restart_closes_proven_resumed_pane_and_keeps_record()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr { SharedRunning = true };
        var terminal = Terminal(fake);
        var (_, launch) = await SavedSharedRecord(terminal, state.Path);
        fake.Replace(Replacement.ServerRestarted);
        fake.ListedAgentName = RestoredAgent;
        Assert.True(HerdrOwnedSessions.Stop(state.Path, ["job-r"], saved => terminal.StopOwnedSessionAsync(saved, CancellationToken.None).GetAwaiter().GetResult()));
        Assert.Single(fake.Calls, c => c.Args is ["pane", "close", "w1:p2"]);
        Assert.True(File.Exists(HerdrOwnedSessions.PathFor(launch)));
    }

    [Fact]
    public async Task SharedPlacement_CreatesRepoWorkspaceOnceAndReusesItUnderConcurrentSpawns()
    {
        var fake = new FakeHerdr { SharedRunning = true, SharedWorkspaceInitiallyAbsent = true, WorkspaceListDelay = TimeSpan.FromMilliseconds(80) };
        var terminal = Terminal(fake);
        var session = await terminal.ExistingSessionAsync("default", CancellationToken.None);
        var first = terminal.OpenAgentTabAsync(session, "agent-a", "/work", Bootstrap, CancellationToken.None);
        var second = terminal.OpenAgentTabAsync(session, "agent-b", "/work", Bootstrap, CancellationToken.None);
        await Task.WhenAll(first, second);

        Assert.Single(fake.Calls, c => c.Args is ["workspace", "create", ..]);
        Assert.Single(fake.Calls, c => c.Args is ["tab", "create", ..]);
        Assert.Contains(fake.Calls, c => c.Args is ["tab", "rename", _, "agent-a"] or ["tab", "rename", _, "agent-b"]);
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["session", "stop" or "delete", ..]);
    }

    [Fact]
    public void WorkspaceLookup_ChoosesLowestNumberAndRefusesUnreadableListing()
    {
        var listing = JsonNode.Parse("""{"result":{"workspaces":[{"label":"repo","workspace_id":"late","number":8},{"label":"repo","workspace_id":"early","number":2}]}}""")!;
        Assert.Equal("early", HerdrTerminal.ResolveWorkspace(listing, "repo"));
        Assert.Throws<HerdrLaunchException>(() => HerdrTerminal.ResolveWorkspace(JsonNode.Parse("""{"result":{"workspaces":[{"label":"repo"}]}}""")!, "repo"));
    }

    [Fact]
    public async Task UnreadableWorkspaceListingNeverCreatesWorkspace()
    {
        var fake = new FakeHerdr { SharedRunning = true, UnreadableWorkspaceList = true };
        var terminal = Terminal(fake);
        var session = await terminal.ExistingSessionAsync("default", CancellationToken.None);
        await Assert.ThrowsAsync<HerdrLaunchException>(() => terminal.OpenAgentTabAsync(session, "agent-a", "/work", Bootstrap, CancellationToken.None));
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["workspace", "create", ..]);
    }

    [Fact]
    public void WorkspaceLabel_UsesMainCheckoutForWorktree()
    {
        using var state = new TempStateDir();
        var repo = state.File("sample-repo");
        Directory.CreateDirectory(repo);
        static void Git(params string[] args)
        {
            var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) { psi.ArgumentList.Add(arg); }
            using var process = Process.Start(psi)!;
            Assert.True(process.WaitForExit(5000) && process.ExitCode == 0, process.StandardError.ReadToEnd());
        }
        Git("init", repo);
        Git("-C", repo, "-c", "user.name=ATF", "-c", "user.email=atf@example.invalid", "commit", "--allow-empty", "-m", "init");
        var worktree = state.File("another-folder");
        Git("-C", repo, "worktree", "add", "--detach", worktree);
        Assert.Equal("sample-repo", HerdrTerminal.WorkspaceLabel(worktree));
    }

    [Fact]
    public async Task SharedTabPinsRelativeCodexHomeEvenWhenServerHasAnotherEnvironment()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr { SharedRunning = true };
        var configured = new Dictionary<string, string?>(Desktop)
        {
            ["CODEX_HOME"] = Path.GetRelativePath(Environment.CurrentDirectory, state.File("codex"))
        };
        var terminal = Terminal(fake, configured);
        var session = await terminal.ExistingSessionAsync("default", CancellationToken.None);
        await terminal.OpenAgentTabAsync(session, "agent-a", "/work", Bootstrap, CancellationToken.None);

        Assert.Equal(state.File("codex"), terminal.Env("CODEX_HOME"));
        var tab = fake.Calls.Single(c => c.Args is ["tab", "create", ..]);
        Assert.Contains("CODEX_HOME=" + state.File("codex"), tab.Args);
    }

    [Fact]
    public async Task SharedPlacement_MissingSessionFailsBeforeCreatingAnything()
    {
        var fake = new FakeHerdr();
        var error = await Assert.ThrowsAsync<HerdrLaunchException>(() => Terminal(fake).ExistingSessionAsync("missing", CancellationToken.None));
        Assert.Contains("not running", error.Message);
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["tab", "create", ..] or ["session", "stop" or "delete", ..]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Agent_start_failure_is_no_effect_only_with_verified_cleanup(bool cleanupFails)
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr
        {
            SharedRunning = true,
            BootstrapFromTab = true,
            AgentStartFails = true,
            PaneCloseFails = cleanupFails,
            SharedWorkspaceLabel = Path.GetFileName(state.Path)
        };
        var launch = new InteractiveLaunch(InteractiveAgentKind.Claude, "atftest", state.Path, null, null, state.File("herdr/bootstrap"))
        { JobId = "job-failed", HerdrPlacement = "herdr-session:default" };
        var error = await Record.ExceptionAsync(() =>
            new HerdrAgentControl(Terminal(fake)).StartAsync(launch, CancellationToken.None));
        if (cleanupFails) { Assert.IsType<HerdrLaunchException>(error); }
        else { Assert.IsType<BackendNotStartedException>(error); }
        Assert.Contains("agent_not_ready", error!.Message);
        Assert.Single(fake.Calls, c => c.Args is ["pane", "close", "w1:p2"]);
        Assert.Equal(cleanupFails, File.Exists(HerdrOwnedSessions.PathFor(launch)));
        Assert.Equal(cleanupFails, File.Exists(launch.BootstrapPath));
    }

    [Fact]
    public async Task Unavailable_terminal_removes_its_bootstrap()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var env = new Dictionary<string, string?>(Desktop) { ["WAYLAND_DISPLAY"] = null };
        var launch = new InteractiveLaunch(InteractiveAgentKind.Claude, "atftest", state.Path, null, null, state.File("herdr/bootstrap"));

        await Assert.ThrowsAsync<BackendNotStartedException>(() => new HerdrAgentControl(Terminal(new FakeHerdr(), env)).StartAsync(launch, CancellationToken.None));

        Assert.False(File.Exists(launch.BootstrapPath));
    }

    [Fact]
    public async Task SharedPlacement_RestartStopUsesDurablePaneRecord()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr { SharedRunning = true, BootstrapFromTab = true, SharedWorkspaceLabel = Path.GetFileName(state.Path) };
        var terminal = Terminal(fake);
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null, state.File("herdr/bootstrap"))
        { JobId = "job-shared", HerdrPlacement = "herdr-session:default" };
        await new HerdrAgentControl(terminal).StartAsync(launch, CancellationToken.None);
        Assert.True(File.Exists(HerdrOwnedSessions.PathFor(launch)));
        Assert.True(new HerdrAgentControl(terminal).StopJobs(state.Path, ["job-shared"]));
        Assert.Single(fake.Calls, c => c.Args is ["pane", "close", "w1:p2"]);
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["session", "stop" or "delete", ..]);
    }

    [Fact]
    public async Task BoundSharedSession_StaysWorkingWhileAnotherServerSharesItsName()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr { SharedRunning = true, BootstrapFromTab = true, SharedWorkspaceLabel = Path.GetFileName(state.Path) };
        var terminal = Terminal(fake);
        var control = new HerdrAgentControl(terminal);
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null, state.File("herdr/bootstrap"))
        { JobId = "job-two", HerdrPlacement = "herdr-session:default" };
        await control.StartAsync(launch, CancellationToken.None);

        fake.ForeignDefaultServer = true;
        fake.AgentStatuses.Enqueue("working");

        Assert.Equal(InteractiveAgentStatus.Working, await control.StatusAsync(launch, CancellationToken.None));
        Assert.False(control.PaneIsGone(launch));
        // Adoption stays exactly-one.
        await Assert.ThrowsAsync<HerdrLaunchException>(() => terminal.ExistingSessionAsync("default", CancellationToken.None));
    }

    [Fact]
    public async Task Status_IsGoneOnlyOnProofAndUnverifiedWhenTheProbeFails()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr { SharedRunning = true, BootstrapFromTab = true, SharedWorkspaceLabel = Path.GetFileName(state.Path) };
        var control = new HerdrAgentControl(Terminal(fake));
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null, state.File("herdr/bootstrap"))
        { JobId = "job-probe", HerdrPlacement = "herdr-session:default" };
        await control.StartAsync(launch, CancellationToken.None);

        fake.PaneGetError = "io_error";
        Assert.Equal(InteractiveAgentStatus.Unverified, await control.StatusAsync(launch, CancellationToken.None));
        fake.PaneGetError = "pane_not_found";
        Assert.Equal(InteractiveAgentStatus.Gone, await control.StatusAsync(launch, CancellationToken.None));
    }

    [Fact]
    public async Task Status_PersistentProbeFailureNeverMasksAReplacedShell()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr { SharedRunning = true, BootstrapFromTab = true, SharedWorkspaceLabel = Path.GetFileName(state.Path) };
        var control = new HerdrAgentControl(Terminal(fake));
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null, state.File("herdr/bootstrap"))
        { JobId = "job-shell", HerdrPlacement = "herdr-session:default" };
        await control.StartAsync(launch, CancellationToken.None);

        fake.PaneGetError = "io_error";
        Assert.Equal(InteractiveAgentStatus.Unverified, await control.StatusAsync(launch, CancellationToken.None));
        fake.Replace(Replacement.ShellReplaced);
        Assert.Equal(InteractiveAgentStatus.Gone, await control.StatusAsync(launch, CancellationToken.None));
    }

    [Fact]
    public async Task StopOwned_RemovesThePanesBootstrapShellProofAndRecord()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr { SharedRunning = true, BootstrapFromTab = true, SharedWorkspaceLabel = Path.GetFileName(state.Path) };
        var control = new HerdrAgentControl(Terminal(fake));
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null, state.File("herdr/atftest.bootstrap"))
        { JobId = "job-stop", HerdrPlacement = "herdr-session:default" };
        await control.StartAsync(launch, CancellationToken.None);
        File.WriteAllText(HerdrTerminal.ShellProofPath(launch.BootstrapPath), "4200");

        control.StopOwned(launch);

        Assert.Contains(fake.Calls, c => c.Args is ["pane", "close", "w1:p2"]);
        Assert.False(File.Exists(launch.BootstrapPath));
        Assert.False(File.Exists(HerdrTerminal.ShellProofPath(launch.BootstrapPath)));
        Assert.False(File.Exists(HerdrOwnedSessions.PathFor(launch)));
    }

    [Fact]
    public async Task StopAgent_KeepsBootstrapProofUntilTheRecordIsForgotten()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr { SharedRunning = true, BootstrapFromTab = true, SharedWorkspaceLabel = Path.GetFileName(state.Path) };
        var terminal = Terminal(fake);
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null, state.File("herdr/atftest.bootstrap"))
        { JobId = "job-agent", HerdrPlacement = "herdr-session:default" };
        await new HerdrAgentControl(terminal).StartAsync(launch, CancellationToken.None);
        File.WriteAllText(HerdrTerminal.ShellProofPath(launch.BootstrapPath), "4200");
        var record = HerdrOwnedSessions.PathFor(launch);
        File.WriteAllText(record + ".gone", "released");

        Assert.True(new HerdrAgentControl(terminal).StopJobs(state.Path, ["job-agent"]));
        Assert.True(File.Exists(record));

        HerdrOwnedSessions.Forget(state.Path, ["job-agent"]);

        Assert.False(File.Exists(record));
        Assert.False(File.Exists(record + ".gone"));
        Assert.False(File.Exists(launch.BootstrapPath));
        Assert.False(File.Exists(HerdrTerminal.ShellProofPath(launch.BootstrapPath)));
    }

    [Fact]
    public async Task FreshStart_ThenShellReplaced_PaneIsGoneAndStopClosesThePane()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr { SharedRunning = true, BootstrapFromTab = true, SharedWorkspaceLabel = Path.GetFileName(state.Path) };
        var control = new HerdrAgentControl(Terminal(fake));
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null, state.File("herdr/bootstrap"))
        { JobId = "job-fresh", HerdrPlacement = "herdr-session:default" };
        await control.StartAsync(launch, CancellationToken.None);

        Assert.False(control.PaneIsGone(launch));
        fake.Replace(Replacement.ShellReplaced);
        Assert.True(control.PaneIsGone(launch));
        control.StopOwned(launch);

        Assert.Contains(fake.Calls, c => c.Args is ["pane", "close", "w1:p2"]);
    }

    [Fact]
    public void PreferSocket_IgnoresAnotherHomesServerWithTheSameName()
    {
        var ours = (new ProcessIdentity(10, 1), new[] { "HOME=/home/u" });
        var scratch = (new ProcessIdentity(20, 2), new[] { "HOME=/tmp/scratch", "XDG_CONFIG_HOME=/tmp/scratch/.config" });
        var socket = "/home/u/.config/herdr/herdr.sock";

        Assert.Equal([ours.Item1], HerdrProcessRunner.PreferSocket([scratch, ours], socket));
        // A layout that matches nothing falls back to the bare name.
        Assert.Equal(2, HerdrProcessRunner.PreferSocket([scratch, ours], "/elsewhere/herdr.sock").Count);
        Assert.Equal(2, HerdrProcessRunner.PreferSocket([scratch, ours], null).Count);
    }

    [Theory]
    [InlineData(Replacement.NewTerminal)]
    [InlineData(Replacement.PaneGone)]
    [InlineData(Replacement.ServerRestarted)]
    [InlineData(Replacement.ShellReplaced)]
    public async Task TabReplaced_BindingInvalid(Replacement replacement)
    {
        var fake = new FakeHerdr();
        var terminal = Terminal(fake);
        var binding = await terminal.OpenAgentTabAsync(await terminal.StartSessionAsync(CancellationToken.None), "agent-a", "/work", Bootstrap, CancellationToken.None);

        fake.Replace(replacement);

        Assert.NotNull(await terminal.VerifyBindingAsync(binding, CancellationToken.None));
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["tab" or "pane", "close", ..] or ["session", "stop" or "delete", ..]);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task BootstrapNotProven_RefusesBinding(bool foreignParent, bool bootstrapMatches)
    {
        var fake = new FakeHerdr { ShellParentIsServer = !foreignParent, ShellBootstrap = bootstrapMatches ? Bootstrap : "/tmp/other" };
        var terminal = Terminal(fake);
        var session = await terminal.StartSessionAsync(CancellationToken.None);

        await Assert.ThrowsAsync<HerdrLaunchException>(() => terminal.OpenAgentTabAsync(session, "agent-a", "/work", Bootstrap, CancellationToken.None));
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["tab" or "pane", "close", ..]);
    }

    [Fact]
    public async Task HiddenShellEnvironment_IsProvenByTheShellsOwnChild()
    {
        using var state = new TempStateDir();
        var bootstrap = state.File("atftest.bootstrap");
        var fake = new FakeHerdr { HideShellEnvironment = true, EnvironmentMayBeHidden = true, BootstrapFromTab = true, ShellProofReporter = 4200 }; // the fake pane shell's PID
        var terminal = Terminal(fake);
        var session = await terminal.StartSessionAsync(CancellationToken.None);

        var binding = await terminal.OpenAgentTabAsync(session, "agent-a", "/work", bootstrap, CancellationToken.None,
            onCreated: created => session = created);

        Assert.Equal(fake.ShellPid, binding.ShellPid);
        Assert.Single(fake.Calls, c => c.Args is ["pane", "run", "w1:p2", HerdrTerminal.ShellProofCommand]);
        var saved = session with { ShellPid = binding.ShellPid, ShellStartTicks = binding.ShellStartTicks };
        Assert.NotNull(await terminal.RebindAsync(saved, bootstrap, CancellationToken.None));
        Assert.Null(await terminal.RebindAsync(saved, state.File("other.bootstrap"), CancellationToken.None));
        fake.Replace(Replacement.ShellReplaced);
        Assert.Null(await terminal.RebindAsync(saved, bootstrap, CancellationToken.None));
    }

    [Theory]
    [InlineData(true, 4999, null)] // a process other than the pane shell answered
    [InlineData(true, null, null)] // the shell never ran the report: times out
    [InlineData(true, 4200, 4300)] // the report never returned the pane to its shell, so `agent start` would refuse it
    [InlineData(false, null, null)] // Linux: an unreadable environment is refused at once, nothing is typed
    public async Task HiddenShellEnvironment_WithoutTheShellsOwnReport_RefusesBinding(bool mayBeHidden, int? reporter, int? foreground)
    {
        using var state = new TempStateDir();
        var fake = new FakeHerdr
        {
            HideShellEnvironment = true,
            EnvironmentMayBeHidden = mayBeHidden,
            BootstrapFromTab = true,
            ShellProofReporter = reporter,
            ForegroundProcessGroup = foreground,
        };
        var terminal = Terminal(fake);
        var session = await terminal.StartSessionAsync(CancellationToken.None);

        await Assert.ThrowsAsync<HerdrLaunchException>(() => terminal.OpenAgentTabAsync(session, "agent-a", "/work", state.File("atftest.bootstrap"), CancellationToken.None));

        Assert.Equal(mayBeHidden ? 1 : 0, fake.Calls.Count(c => c.Args is ["pane", "run", ..]));
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["tab" or "pane", "close", ..]);
    }

    [Fact]
    public async Task ShellProofCommand_ReportsItsParentShellAtTheInheritedBootstrap()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX shells only");
        using var state = new TempStateDir();
        var bootstrap = state.File("it's \"quoted\" $HOME.bootstrap");
        // The trailing ':' keeps sh from exec'ing the proof in place, as an interactive pane shell never does.
        var shell = new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", HerdrTerminal.ShellProofCommand + "; :" } };
        shell.Environment[HerdrTerminal.BootstrapVariable] = bootstrap;
        using var process = Process.Start(shell)!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(HerdrTerminal.ShellProofPath(bootstrap)));
    }

    [Fact]
    public async Task ServerLaunch_LeavesTheDaemonsProcessGroup()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Herdr launches are POSIX only");
        using var state = new TempStateDir();
        var bin = state.File("bin");
        Directory.CreateDirectory(bin);
        // Stands in for `herdr --session NAME server`: records its PID and process group, then stays alive.
        File.WriteAllText(Path.Combine(bin, "herdr"), "#!/bin/sh\necho $$ $(ps -o pgid= -p $$) > \"$HOME/server.tmp\"\nmv \"$HOME/server.tmp\" \"$HOME/server\"\nexec sleep 60\n");
        File.SetUnixFileMode(Path.Combine(bin, "herdr"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var seed = new Dictionary<string, string?> { ["PATH"] = bin + ":/usr/bin:/bin", ["HOME"] = state.Path };

        await new HerdrProcessRunner().StartDetachedAsync(HerdrCommands.SharedServerStartInfo("atf-test-pgid", seed, null, state.Path),
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var report = state.File("server");
        for (var wait = Stopwatch.StartNew(); !File.Exists(report) && wait.Elapsed < TimeSpan.FromSeconds(10);) { await Task.Delay(50, TestContext.Current.CancellationToken); }
        var fields = File.ReadAllText(report).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var (pid, group) = (int.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            using var ps = Process.Start(new ProcessStartInfo("ps", ["-o", "pgid=", "-p", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)]) { RedirectStandardOutput = true })!;
            var ours = int.Parse((await ps.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(pid, group);
            Assert.NotEqual(ours, group);
        }
        finally
        {
            using var server = Process.GetProcessById(pid);
            server.Kill();
        }
    }

    [Fact]
    public async Task SharedPlacement_CreatesItsLockPrivateAndRepairsAnUnopenableOne()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "flock lock files are POSIX only");
        using var state = new TempStateDir();
        var config = state.File("herdr");
        Directory.CreateDirectory(config);
        HerdrTerminal Shared(FakeHerdr fake) => new(new HerdrTerminalOptions
        {
            Environment = new Dictionary<string, string?>(Desktop) { ["HERDR_CONFIG_PATH"] = Path.Combine(config, "config.toml") },
            StartupTimeout = TimeSpan.FromMilliseconds(400),
            PollInterval = TimeSpan.FromMilliseconds(10),
        }, fake);

        var first = Shared(new FakeHerdr { SharedRunning = true });
        await first.OpenAgentTabAsync(await first.ExistingSessionAsync("default", CancellationToken.None), "agent-a", "/work", Bootstrap, CancellationToken.None);
        var lockFile = Assert.Single(Directory.GetFiles(config, "win-agent-teams-default.ws-*.lock"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(lockFile));

        // A lock file an earlier macOS build created with a garbage mode is ours to repair, not a permanent EACCES.
        File.SetUnixFileMode(lockFile, UnixFileMode.None);
        var second = Shared(new FakeHerdr { SharedRunning = true });
        await second.OpenAgentTabAsync(await second.ExistingSessionAsync("default", CancellationToken.None), "agent-a", "/work", Bootstrap, CancellationToken.None);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(lockFile));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task NoVisibleProvider_NoHeadlessFallback(bool display, bool herdrInstalled)
    {
        var env = new Dictionary<string, string?>(Desktop);
        if (!display)
        {
            env.Remove("WAYLAND_DISPLAY");
        }
        var fake = new FakeHerdr { Installed = herdrInstalled };
        if (OperatingSystem.IsMacOS() && herdrInstalled)
        {
            // macOS always has a visible desktop; X11/Wayland variables mean nothing there.
            await Terminal(fake, env).StartSessionAsync(CancellationToken.None);
            Assert.Single(fake.Detached);
            return;
        }

        var error = await Assert.ThrowsAsync<InteractiveTerminalUnavailableException>(() => Terminal(fake, env).StartSessionAsync(CancellationToken.None));

        Assert.NotEmpty(error.Message);
        Assert.Empty(fake.Detached);
        Assert.All(fake.Calls, c => Assert.Equal(["--version"], c.Args));
    }

    [Fact]
    public async Task MissingDisplay_IsFilledFromSessionEnvironment_OrStillRefused()
    {
        if (OperatingSystem.IsMacOS()) { return; }
        var env = new Dictionary<string, string?>(Desktop);
        env.Remove("WAYLAND_DISPLAY");

        var refused = new FakeHerdr();
        await Assert.ThrowsAsync<InteractiveTerminalUnavailableException>(() =>
            Terminal(refused, env, () => new Dictionary<string, string>()).StartSessionAsync(CancellationToken.None));
        Assert.Empty(refused.Detached);

        var fake = new FakeHerdr();
        await Terminal(fake, env, () => new Dictionary<string, string> { ["WAYLAND_DISPLAY"] = "wayland-9" }).StartSessionAsync(CancellationToken.None);
        Assert.Equal("wayland-9", Assert.Single(fake.Detached).Environment["WAYLAND_DISPLAY"]);
    }

    [Fact]
    public async Task Teardown_StopsOnlyProvenOwnedSession()
    {
        var fake = new FakeHerdr();
        var terminal = Terminal(fake);
        var session = await terminal.StartSessionAsync(CancellationToken.None);

        fake.Replace(Replacement.ServerRestarted);
        await Assert.ThrowsAsync<HerdrLaunchException>(() => terminal.StopOwnedSessionAsync(session, CancellationToken.None));
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["session", "stop" or "delete", ..]);

        var fresh = new FakeHerdr();
        var owned = Terminal(fresh);
        await owned.StopOwnedSessionAsync(await owned.StartSessionAsync(CancellationToken.None), CancellationToken.None);
        Assert.Equal(["stop", "delete"], fresh.Calls.Where(c => c.Args is ["session", "stop" or "delete", ..]).Select(c => c.Args[1]));
    }

    [Fact]
    public async Task Restart_stop_retries_durable_identity_and_refuses_a_reused_server()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr();
        var terminal = Terminal(fake);
        var owned = await terminal.StartSessionAsync(CancellationToken.None);
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null,
            Path.Combine(state.Path, "herdr", "atftest.bootstrap"))
        { JobId = "job-owned" };
        Directory.CreateDirectory(Path.GetDirectoryName(launch.BootstrapPath)!);
        HerdrOwnedSessions.Save(launch, owned);
        var attempts = 0;
        Assert.Throws<IOException>(() => HerdrOwnedSessions.Stop(state.Path, ["job-owned"], _ =>
        {
            attempts++;
            throw new IOException("temporary");
        }));
        Assert.True(File.Exists(HerdrOwnedSessions.PathFor(launch)));
        Assert.True(HerdrOwnedSessions.Stop(state.Path, ["job-owned"], saved =>
        {
            attempts++;
            Assert.Equal(owned.ServerStartTicks, saved.ServerStartTicks);
            terminal.StopOwnedSessionAsync(saved, CancellationToken.None).GetAwaiter().GetResult();
        }));
        Assert.Equal(2, attempts);
        // The proof survives even a crash between successful stop and DB reconciliation.
        Assert.True(File.Exists(HerdrOwnedSessions.PathFor(launch)));
        fake.Replace(Replacement.ServerRestarted);
        var stopsBefore = fake.Calls.Count(c => c.Args is ["session", "stop", ..]);
        Assert.Throws<HerdrLaunchException>(() => HerdrOwnedSessions.Stop(state.Path, ["job-owned"], saved =>
            terminal.StopOwnedSessionAsync(saved, CancellationToken.None).GetAwaiter().GetResult()));
        Assert.Equal(stopsBefore, fake.Calls.Count(c => c.Args is ["session", "stop", ..]));
        Assert.True(File.Exists(HerdrOwnedSessions.PathFor(launch)));
    }

    [Fact]
    public async Task Proven_gone_pane_of_a_terminal_job_releases_the_fence_every_start_keeps_its_record_and_reports_once()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        using var f = new AgentTeamForge.Tests.Support.JobFixture();
        var fake = new FakeHerdr { SharedRunning = true, BootstrapFromTab = true, SharedWorkspaceLabel = Path.GetFileName(state.Path) };
        var terminal = Terminal(fake);
        var job = f.Submit("owned");
        var claim = f.Store.BeginNextAttempt()!;
        var run = new AgentTeamForge.DAL.Features.Jobs.RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation);
        f.Store.RecordSession(run, "native-owned");
        f.Store.Complete(run, "result kept");
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null, state.File("herdr/atftest.bootstrap"))
        { JobId = job.JobId, HerdrPlacement = "herdr-session:default" };
        await new HerdrAgentControl(terminal).StartAsync(launch, CancellationToken.None);
        fake.Replace(Replacement.ShellReplaced);
        var backend = new HerdrInteractiveBackend(terminal, InteractiveAgentKind.Codex, state.Path);

        // Every start fences each job that has an ownership record.
        HerdrOwnedSessions.Recover(state.Path, f.Store.FenceSession, _ => { });
        Assert.Contains(job.JobId, f.Store.FencedTerminalJobs());
        var owner = f.Store.GetJob(job.JobId)!;
        Assert.Equal(PaneOwnerRecovery.Gone, backend.RecoverTerminalOwner(owner, [],
            () => f.Store.ReleaseRestartFence(owner.JobId, owner.SessionId)));

        Assert.True(File.Exists(HerdrOwnedSessions.PathFor(launch)));
        // The bootstrap only proved the pane, which is gone.
        Assert.False(File.Exists(launch.BootstrapPath));
        HerdrOwnedSessions.Recover(state.Path, f.Store.FenceSession, _ => { });
        Assert.Contains(job.JobId, f.Store.FencedTerminalJobs());
        Assert.Equal(PaneOwnerRecovery.GoneAgain, backend.RecoverTerminalOwner(owner, [],
            () => f.Store.ReleaseRestartFence(owner.JobId, owner.SessionId)));
        Assert.DoesNotContain(job.JobId, f.Store.FencedTerminalJobs());
        Assert.True(File.Exists(HerdrOwnedSessions.PathFor(launch)));
        Assert.Equal("result kept", f.Store.GetJob(job.JobId)!.ResultText);
    }

    [Fact]
    public async Task Stop_agent_after_restart_releases_fence_without_changing_job_outcome()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        using var f = new AgentTeamForge.Tests.Support.JobFixture();
        var fake = new FakeHerdr();
        var terminal = Terminal(fake);
        var owned = await terminal.StartSessionAsync(CancellationToken.None);
        var job = f.Submit("owned");
        var claim = f.Store.BeginNextAttempt()!;
        var run = new AgentTeamForge.DAL.Features.Jobs.RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation);
        f.Store.RecordSession(run, "native-owned");
        f.Store.Complete(run, "result kept");
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null,
            Path.Combine(state.Path, "herdr", "atftest.bootstrap"))
        { JobId = job.JobId };
        Directory.CreateDirectory(Path.GetDirectoryName(launch.BootstrapPath)!);
        HerdrOwnedSessions.Save(launch, owned);
        HerdrOwnedSessions.Recover(state.Path, f.Store.FenceSession, _ => { });
        Assert.True(f.Store.IsSessionFenced(job.JobId));
        var backend = new HerdrInteractiveBackend(terminal, InteractiveAgentKind.Codex, state.Path);
        var catalog = new AgentTeamForge.Business.Features.Agents.Backends.BackendCatalog().Register("fake", () => backend);
        var stop = new AgentTeamForge.Business.Features.Jobs.StopAgent(f.Store, AgentTeamForge.Tests.Support.JobFixture.Operator, catalog);

        Assert.Equal("agent_stopped", stop.Execute(job.JobId).Outcome);
        Assert.False(f.Store.IsSessionFenced(job.JobId));
        Assert.False(File.Exists(HerdrOwnedSessions.PathFor(launch)));
        Assert.Equal("result kept", f.Store.GetJob(job.JobId)!.ResultText);
        Assert.Equal("agent_not_running", stop.Execute(job.JobId).Outcome);
        Assert.False(f.Store.IsSessionFenced(job.JobId));
        Assert.Single(fake.Calls, c => c.Args is ["session", "stop", ..]);
    }

    [Fact]
    public async Task Uncertain_turn_without_native_session_keeps_tab_fenced_until_stop_agent()
    {
        var state = Path.Combine("/tmp", "atf-herdr-nonative-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        try
        {
            using var f = new AgentTeamForge.Tests.Support.JobFixture();
            var fake = new FakeHerdr { BootstrapFromTab = true };
            // No transcript ever appears, so no native session ID is learned.
            var backend = new HerdrInteractiveBackend(new HerdrAgentControl(Terminal(fake)), new InteractiveTranscriptReader(name => name == "CODEX_HOME" ? state : null),
                InteractiveAgentKind.Codex, state, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(600));
            var job = f.Submit("no native id");
            var claim = f.Store.BeginNextAttempt()!;
            using (var dispatcher = new AgentTeamForge.Business.Features.Jobs.DispatchJob(f.Store, backend, f.Limits,
                AgentTeamForge.DAL.Sqlite.DurabilityCheckpoints.None, f.Admission, _ => { }))
            {
                await dispatcher.RunAttemptAsync(claim, TestContext.Current.CancellationToken);
            }

            var uncertain = f.Store.GetJob(job.JobId)!;
            Assert.Equal(AgentTeamForge.DAL.Features.Jobs.JobStatus.NeedsReconciliation, uncertain.Status);
            Assert.Null(uncertain.SessionId);
            Assert.True(f.Store.IsSessionFenced(job.JobId));
            Assert.DoesNotContain(fake.Calls, c => c.Args is ["session", "stop", ..]);
            Assert.True(backend.HasOwnedJobs([job.JobId]));

            var catalog = new AgentTeamForge.Business.Features.Agents.Backends.BackendCatalog().Register("fake", () => backend);
            var stop = new AgentTeamForge.Business.Features.Jobs.StopAgent(f.Store, AgentTeamForge.Tests.Support.JobFixture.Operator, catalog);
            Assert.Equal("agent_stopped", stop.Execute(job.JobId).Outcome);
            Assert.False(f.Store.IsSessionFenced(job.JobId));
            Assert.False(backend.HasOwnedJobs([job.JobId]));
            Assert.Single(fake.Calls, c => c.Args is ["session", "stop", ..]);
        }
        finally { Directory.Delete(state, recursive: true); }
    }

    static (HerdrInteractiveBackend Backend, BackendCatalog Catalog, FakeHerdr Fake) RecordlessHerdr(string state)
    {
        var fake = new FakeHerdr();
        var backend = new HerdrInteractiveBackend(Terminal(fake), InteractiveAgentKind.Codex, state);
        return (backend, new BackendCatalog().Register("fake", () => backend), fake);
    }

    [Fact]
    public void Reconciled_job_without_ownership_record_is_cancelled_by_stop_job()
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var (_, catalog, fake) = RecordlessHerdr(state.Path);
        var job = f.Submit("stuck");
        var claim = f.Store.BeginNextAttempt()!;
        Assert.True(f.Store.EndUnsuccessfully(new RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation),
            JobStatus.NeedsReconciliation, "interactive_agent_exited"));
        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });

        var result = new StopJob(f.Store, JobFixture.Operator, dispatcher.CancelRunning,
            stopReconciled: dispatcher.StopReconciled, forgetReconciledOwnership: dispatcher.ForgetReconciledOwnership).Execute(job.JobId);

        Assert.Equal("stopped", result.Outcome);
        var stopped = f.Store.GetJob(job.JobId)!;
        Assert.Equal((JobStatus.Cancelled, "stopped"), (stopped.Status, stopped.ReasonCode));
        Assert.False(f.Store.IsSessionFenced(job.JobId));
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["session", "stop", ..]);
    }

    [Fact]
    public void Reconciled_job_without_record_is_not_cancelled_while_a_session_peer_is_queued()
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var (_, catalog, _) = RecordlessHerdr(state.Path);
        var parent = f.Submit("stuck");
        var claim = f.Store.BeginNextAttempt()!;
        var run = new RunRef(parent.JobId, claim.RunId, claim.Generation, claim.Correlation);
        f.Store.RecordSession(run, "native-peer");
        f.Store.Complete(run, "done");
        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        var followUp = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept())
            .Execute(new FollowUpRequest(parent.JobId, "next", "peer-key"));
        Assert.Null(followUp.Error);
        Assert.Contains(f.Store.GetSessionJobs(parent.JobId), id => id != parent.JobId);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET status='needs_reconciliation' WHERE job_id=$id";
            command.Parameters.AddWithValue("$id", parent.JobId);
            command.ExecuteNonQuery();
        }

        var result = new StopJob(f.Store, JobFixture.Operator, dispatcher.CancelRunning,
            stopReconciled: dispatcher.StopReconciled, forgetReconciledOwnership: dispatcher.ForgetReconciledOwnership).Execute(parent.JobId);

        Assert.Equal(JobErrors.OwnershipNotProven, result.Error);
        Assert.Equal(JobStatus.NeedsReconciliation, f.Store.GetJob(parent.JobId)!.Status);
    }

    [Fact]
    public async Task Follow_up_admitted_after_the_record_vanished_keeps_the_job_uncancelled()
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var (_, catalog, _) = RecordlessHerdr(state.Path);
        var parent = f.Submit("stuck");
        var claim = f.Store.BeginNextAttempt()!;
        var run = new RunRef(parent.JobId, claim.RunId, claim.Generation, claim.Correlation);
        f.Store.RecordSession(run, "native-race");
        Assert.True(f.Store.EndUnsuccessfully(run, JobStatus.NeedsReconciliation, "interactive_agent_exited"));
        f.Store.ReconcileStoppedJob(parent.JobId); // legacy row: reconciled but unfenced
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null,
            Path.Combine(state.Path, "herdr", "atftest.bootstrap"))
        { JobId = parent.JobId };
        Directory.CreateDirectory(Path.GetDirectoryName(launch.BootstrapPath)!);
        HerdrOwnedSessions.Save(launch, await Terminal(new FakeHerdr()).StartSessionAsync(CancellationToken.None));
        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        JobResult? raced = null;

        var result = new StopJob(f.Store, JobFixture.Operator, dispatcher.CancelRunning,
            stopReconciled: job =>
            {
                // Teardown deletes the record after the stop began but before StopReconciled looks.
                File.Delete(HerdrOwnedSessions.PathFor(launch));
                var ok = dispatcher.StopReconciled(job);
                raced = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept())
                    .Execute(new FollowUpRequest(parent.JobId, "late", "late-key"));
                return ok;
            },
            forgetReconciledOwnership: dispatcher.ForgetReconciledOwnership).Execute(parent.JobId);

        Assert.Null(raced!.Error);
        Assert.Equal(JobErrors.OwnershipNotProven, result.Error);
        Assert.Equal(JobStatus.NeedsReconciliation, f.Store.GetJob(parent.JobId)!.Status);
    }

    [Fact]
    public void Refused_stop_releases_only_its_own_fence_when_a_peer_is_already_fenced()
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var (_, catalog, _) = RecordlessHerdr(state.Path);
        var b = f.Submit("peer-b");
        var claim = f.Store.BeginNextAttempt()!;
        var run = new RunRef(b.JobId, claim.RunId, claim.Generation, claim.Correlation);
        f.Store.RecordSession(run, "native-shared");
        f.Store.Complete(run, "done");
        var followUps = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept());
        var a = followUps.Execute(new FollowUpRequest(b.JobId, "a", "a-key")).Job!;
        var aClaim = f.Store.BeginNextAttempt()!;
        Assert.True(f.Store.EndUnsuccessfully(new RunRef(a.JobId, aClaim.RunId, aClaim.Generation, aClaim.Correlation),
            JobStatus.NeedsReconciliation, "interactive_agent_exited"));
        f.Store.ReconcileStoppedJob(a.JobId);
        var c = followUps.Execute(new FollowUpRequest(b.JobId, "c", "c-key"));
        Assert.Null(c.Error);
        f.Store.FenceSession(b.JobId);
        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });

        var result = new StopJob(f.Store, JobFixture.Operator, dispatcher.CancelRunning,
            stopReconciled: dispatcher.StopReconciled, forgetReconciledOwnership: dispatcher.ForgetReconciledOwnership).Execute(a.JobId);

        Assert.Equal(JobErrors.OwnershipNotProven, result.Error);
        f.Store.ReconcileStoppedJob(b.JobId);
        f.Store.Cancel(c.Job!.JobId, JobFixture.Operator.Principal, JobFixture.Operator.Team, false);
        Assert.False(f.Store.IsSessionFenced(a.JobId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_cancel_transaction_leaves_the_fence_and_status_untouched(bool fencedBefore)
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var (_, catalog, _) = RecordlessHerdr(state.Path);
        var job = f.Submit("stuck");
        var claim = f.Store.BeginNextAttempt()!;
        Assert.True(f.Store.EndUnsuccessfully(new RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation),
            JobStatus.NeedsReconciliation, "interactive_agent_exited"));
        if (!fencedBefore) { f.Store.ReconcileStoppedJob(job.JobId); }
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER no_cancel BEFORE UPDATE OF status ON jobs WHEN NEW.status='cancelled' BEGIN SELECT RAISE(ABORT,'write failed'); END";
            command.ExecuteNonQuery();
        }
        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });

        var result = new StopJob(f.Store, JobFixture.Operator, dispatcher.CancelRunning, stopReconciled: dispatcher.StopReconciled,
            forgetReconciledOwnership: dispatcher.ForgetReconciledOwnership).Execute(job.JobId);

        Assert.NotNull(result.Error);
        Assert.Equal(JobStatus.NeedsReconciliation, f.Store.GetJob(job.JobId)!.Status);
        Assert.Equal(fencedBefore, f.Store.IsSessionFenced(job.JobId));
    }

    [Fact]
    public async Task Owned_reconciled_job_stop_cancels_its_deferred_child_with_the_parent()
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var fake = new FakeHerdr();
        var terminal = Terminal(fake);
        var owned = await terminal.StartSessionAsync(CancellationToken.None);
        var parent = f.Submit("owned");
        var claim = f.Store.BeginNextAttempt()!;
        var run = new RunRef(parent.JobId, claim.RunId, claim.Generation, claim.Correlation);
        f.Store.RecordSession(run, "native-owned");
        var deferred = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept())
            .Execute(new FollowUpRequest(parent.JobId, "later", "defer-key") { Defer = true });
        Assert.Null(deferred.Error);
        Assert.True(f.Store.EndUnsuccessfully(run, JobStatus.NeedsReconciliation, "interactive_completion_unobserved"));
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null,
            Path.Combine(state.Path, "herdr", "atftest.bootstrap"))
        { JobId = parent.JobId };
        Directory.CreateDirectory(Path.GetDirectoryName(launch.BootstrapPath)!);
        HerdrOwnedSessions.Save(launch, owned);
        var backend = new HerdrInteractiveBackend(terminal, InteractiveAgentKind.Codex, state.Path);
        var catalog = new BackendCatalog().Register("fake", () => backend);
        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });

        var result = new StopJob(f.Store, JobFixture.Operator, dispatcher.CancelRunning, stopReconciled: dispatcher.StopReconciled,
            forgetReconciledOwnership: dispatcher.ForgetReconciledOwnership).Execute(parent.JobId);

        Assert.Equal("stopped", result.Outcome);
        Assert.Equal(JobStatus.Cancelled, f.Store.GetJob(parent.JobId)!.Status);
        Assert.Equal(JobStatus.Cancelled, f.Store.GetJob(deferred.Job!.JobId)!.Status);
        Assert.Single(fake.Calls, c => c.Args is ["session", "stop", ..]);
    }

    [Fact]
    public void Stop_agent_cancels_reconciled_job_without_ownership_record()
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var (_, catalog, _) = RecordlessHerdr(state.Path);
        var job = f.Submit("stuck");
        var claim = f.Store.BeginNextAttempt()!;
        Assert.True(f.Store.EndUnsuccessfully(new RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation),
            JobStatus.NeedsReconciliation, "interactive_agent_exited"));

        Assert.Equal("agent_not_running", new StopAgent(f.Store, JobFixture.Operator, catalog).Execute(job.JobId).Outcome);
        Assert.Equal(JobStatus.Cancelled, f.Store.GetJob(job.JobId)!.Status);
    }

    [Fact]
    public async Task Command_OutputAndTimeAreBounded()
    {
        var runner = new HerdrProcessRunner();
        var flood = await runner.CaptureAsync(Sh("head -c 8388608 /dev/zero; exit 3"), TimeSpan.FromSeconds(15), 64 * 1024, 1024, CancellationToken.None);
        Assert.Equal((false, 3, true, 64 * 1024), (flood.TimedOut, flood.ExitCode, flood.StdoutTruncated, flood.Stdout.Length));

        var clock = Stopwatch.StartNew();
        var hung = await runner.CaptureAsync(Sh("sleep 5 & echo started"), TimeSpan.FromMilliseconds(400), 1024, 1024, CancellationToken.None);
        Assert.True(hung.TimedOut);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4));

        // A truncated reply is refused rather than parsed.
        var fake = new FakeHerdr { FloodSessionList = true };
        await Assert.ThrowsAsync<HerdrLaunchException>(() => Terminal(fake).StartSessionAsync(CancellationToken.None));
        Assert.Empty(fake.Detached);
    }

    [Theory]
    [InlineData(InteractiveAgentKind.Codex)]
    [InlineData(InteractiveAgentKind.Claude)]
    [InlineData(InteractiveAgentKind.Pi)]
    public async Task Prompt_waits_for_stable_ready_state_before_sending(InteractiveAgentKind kind)
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr { BootstrapFromTab = true, AgentStatuses = new Queue<string>(["unknown", "idle", "working", "done"]) };
        var control = new HerdrAgentControl(Terminal(fake));
        var phases = new List<string>();
        var launch = new InteractiveLaunch(kind, "atftest", state.Path, "resumed-native", state.Path, Path.Combine(state.Path, "bootstrap"))
        { StartupProgress = phase => phases.Add(phase) };
        await control.StartAsync(launch, TestContext.Current.CancellationToken);
        await control.PromptAsync(launch, "one prompt", TestContext.Current.CancellationToken);
        Assert.Equal(["ready", "submitted"], phases);
        Assert.Equal(kind == InteractiveAgentKind.Claude,
            fake.Calls.Single(c => c.Args is ["tab", "create", ..]).Args.Contains("CLAUDE_CODE_SANDBOXED=1"));
        var promptIndex = fake.Calls.FindIndex(c => c.Args is ["--session", _, "agent", "prompt", ..]);
        Assert.True(fake.Calls.Take(promptIndex).Count(c => c.Args is ["agent", "get", ..]) >= 8);
        Assert.Single(fake.Calls, c => c.Args is ["--session", _, "agent", "prompt", ..]);
    }

    [Theory]
    [InlineData(InteractiveAgentKind.Claude, "Choose the text style that looks best with your terminal", "agent_first_run_required")]
    [InlineData(InteractiveAgentKind.Claude, "Choose the theme\nDark mode", "agent_first_run_required")]
    [InlineData(InteractiveAgentKind.Claude, "Try the new fullscreen renderer?", "agent_first_run_required")]
    [InlineData(InteractiveAgentKind.Claude, "Select login method", "agent_login_required")]
    [InlineData(InteractiveAgentKind.Claude, "Not logged in · Please run /login", "agent_login_required")]
    [InlineData(InteractiveAgentKind.Claude, "Claude account with subscription\nAnthropic Console account", "agent_login_required")]
    [InlineData(InteractiveAgentKind.Claude, "Yes, I trust this folder", "agent_workspace_trust_required")]
    [InlineData(InteractiveAgentKind.Codex, "Sign in with ChatGPT\nUse an API key", "agent_login_required")]
    [InlineData(InteractiveAgentKind.Codex, "Do you trust the contents of this directory", "agent_workspace_trust_required")]
    [InlineData(InteractiveAgentKind.Pi, PiNoModelsScreen, "agent_login_required")]
    public async Task Recognized_startup_blocker_fails_without_delivery_or_fence(InteractiveAgentKind kind, string screen, string reason)
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var fake = new FakeHerdr { BootstrapFromTab = true, Screen = screen };
        var backend = new HerdrInteractiveBackend(new HerdrAgentControl(Terminal(fake)), new InteractiveTranscriptReader(_ => state.Path), kind, state.Path);
        var job = f.Submit("blocked");
        var claim = f.Store.BeginNextAttempt()!;
        using var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        var clock = Stopwatch.StartNew();
        await dispatcher.RunAttemptAsync(claim, TestContext.Current.CancellationToken);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(JobStatus.Failed, f.Store.GetJob(job.JobId)!.Status);
        Assert.Equal(reason, f.Store.GetJob(job.JobId)!.ReasonCode);
        Assert.False(f.Store.IsSessionFenced(job.JobId));
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["--session", _, "agent", "prompt", ..]);
        Assert.Contains(fake.Calls, c => c.Args is ["session", "stop", ..]);
        Assert.Null(f.Store.GetRuns(job.JobId).Single().SubmittedAt);
    }

    // Pi 0.99 signed out: the editor renders, with its own warning above it.
    const string PiEditor = "\n────────\n\n────────\n/private/tmp/w\n0.0%/0 (auto)    unknown";
    const string PiNoModelsScreen = " Warning: No models available. Use /login to log into a provider via OAuth or\n API key. See:\n /opt/pi/docs/providers.md" + PiEditor;
    const string PiNoKeyError = " Error: No API key found for the selected model.\n\n Use /login to log into a provider via OAuth or API key. See:\n /opt/pi/docs/providers.md\n";

    [Fact]
    public void Pi_login_is_only_its_own_new_error_line()
    {
        var blocker = HerdrAgentControl.StartupBlocker(InteractiveAgentKind.Pi, PiNoModelsScreen);
        Assert.Equal("agent_login_required", blocker?.Reason);
        Assert.Contains("run `pi` and /login", blocker!.Message);
        Assert.Contains("No models available", blocker.Message);
        // A resumed session's history is not a startup screen; prose quoting the error is not Pi's error line.
        Assert.Null(HerdrAgentControl.StartupBlocker(InteractiveAgentKind.Pi, PiNoModelsScreen, resumed: true));
        Assert.Null(HerdrAgentControl.StartupBlocker(InteractiveAgentKind.Pi, "Pi said: Error: No API key found" + PiEditor));
        Assert.Null(HerdrAgentControl.StartupBlocker(InteractiveAgentKind.Codex, PiNoModelsScreen));

        // After the prompt only an error line below the last one already on screen counts.
        var after = PiNoKeyError + PiEditor;
        var none = HerdrAgentControl.PiLoginAnchor(PiEditor);
        Assert.Empty(none);
        Assert.Equal("agent_login_required", HerdrAgentControl.DeliveryBlocker(InteractiveAgentKind.Pi, after, none)?.Reason);
        Assert.Contains("No API key found for the selected model.", HerdrAgentControl.DeliveryBlocker(InteractiveAgentKind.Pi, after, none)!.Message);
        Assert.Null(HerdrAgentControl.DeliveryBlocker(InteractiveAgentKind.Pi, after, HerdrAgentControl.PiLoginAnchor(after)));
        Assert.Equal("agent_login_required", HerdrAgentControl.DeliveryBlocker(InteractiveAgentKind.Pi, PiNoKeyError + after, HerdrAgentControl.PiLoginAnchor(after))?.Reason);
        Assert.Null(HerdrAgentControl.DeliveryBlocker(InteractiveAgentKind.Pi, PiEditor, none));
        Assert.Null(HerdrAgentControl.DeliveryBlocker(InteractiveAgentKind.Codex, after, none));
    }

    [Fact]
    public void Pi_login_line_is_placed_by_content_in_a_sliding_window_not_counted()
    {
        static string History(int from, int to) => string.Concat(Enumerable.Range(from, to - from).Select(i => $" turn {i} output\n"));
        var before = History(0, 6) + PiNoKeyError + History(6, 10) + PiEditor;
        var anchor = HerdrAgentControl.PiLoginAnchor(before);

        // The old error scrolled out of the 100-line window as a new one arrived: the count is unchanged.
        var replaced = History(8, 10) + " > follow-up prompt\n" + PiNoKeyError + PiEditor;
        Assert.Equal("agent_login_required", HerdrAgentControl.DeliveryBlocker(InteractiveAgentKind.Pi, replaced, anchor)?.Reason);
        // The old error is still on screen, only its context above was cut off: nothing new.
        var scrolled = History(5, 6) + PiNoKeyError + History(6, 10) + " > follow-up prompt\n working\n" + PiEditor;
        Assert.Null(HerdrAgentControl.DeliveryBlocker(InteractiveAgentKind.Pi, scrolled, anchor));
        // The same error text again below the old one is new.
        Assert.Equal("agent_login_required", HerdrAgentControl.DeliveryBlocker(InteractiveAgentKind.Pi, scrolled + "\n" + PiNoKeyError, anchor)?.Reason);
    }

    [Fact]
    public async Task Recovered_pi_run_never_reads_old_screen_errors_as_its_own()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        // A retained pane's history: an earlier signed-out turn, since fixed by /login.
        var fake = new FakeHerdr { BootstrapFromTab = true, Screen = PiNoKeyError + " > earlier prompt\n" + PiEditor };
        var control = new HerdrAgentControl(Terminal(fake));
        var launch = new InteractiveLaunch(InteractiveAgentKind.Pi, "atftest", state.Path, null, null, Path.Combine(state.Path, "bootstrap"));
        await control.StartAsync(launch, TestContext.Current.CancellationToken);

        // A restarted daemon observes this pane without having prompted it: there is no pre-prompt screen to compare.
        Assert.Null(await control.DeliveryBlockerAsync(launch, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pi_refusing_the_prompt_for_lack_of_a_key_fails_fast_and_closes_its_pane()
    {
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var fake = new FakeHerdr { BootstrapFromTab = true, Screen = PiEditor, ScreenAfterPrompt = PiNoKeyError + PiEditor };
        var backend = new HerdrInteractiveBackend(new HerdrAgentControl(Terminal(fake)), new InteractiveTranscriptReader(_ => state.Path), InteractiveAgentKind.Pi, state.Path);
        var job = f.Submit("pi-signed-out");
        var claim = f.Store.BeginNextAttempt()!;
        using var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        var clock = Stopwatch.StartNew();
        await dispatcher.RunAttemptAsync(claim, TestContext.Current.CancellationToken);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        var record = f.Store.GetJob(job.JobId)!;
        Assert.Equal(JobStatus.Failed, record.Status);
        Assert.Equal("agent_login_required", record.ReasonCode);
        Assert.Single(fake.Calls, c => c.Args is ["--session", _, "agent", "prompt", ..]);
        Assert.Contains(fake.Calls, c => c.Args is ["session", "stop", ..]);
    }

    [Fact]
    public async Task Unknown_screen_keeps_waiting_without_submitting()
    {
        using var state = new TempStateDir();
        var fake = new FakeHerdr { BootstrapFromTab = true, Screen = "Loading something unfamiliar" };
        using var f = new JobFixture();
        var backend = new HerdrInteractiveBackend(new HerdrAgentControl(Terminal(fake), TimeSpan.FromMilliseconds(300)),
            new InteractiveTranscriptReader(_ => state.Path), InteractiveAgentKind.Codex, state.Path,
            startupTimeout: TimeSpan.FromMilliseconds(300));
        var job = f.Submit("unknown");
        var claim = f.Store.BeginNextAttempt()!;
        using var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        await dispatcher.RunAttemptAsync(claim, TestContext.Current.CancellationToken);
        Assert.Equal(JobStatus.NeedsReconciliation, f.Store.GetJob(job.JobId)!.Status);
        Assert.Equal("interactive_delivery_not_confirmed", f.Store.GetJob(job.JobId)!.ReasonCode);
        Assert.True(f.Store.IsSessionFenced(job.JobId));
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["--session", _, "agent", "prompt", ..]);
        Assert.Null(HerdrAgentControl.StartupBlocker(InteractiveAgentKind.Pi, "Select login method"));
        Assert.Null(HerdrAgentControl.StartupBlocker(InteractiveAgentKind.Claude, "Choose the text style\n❯\nbypass permissions on"));
    }

    [Fact]
    public async Task Setup_text_in_history_never_blocks_a_ready_resumed_or_retained_pane()
    {
        const string editor = "\n────\n❯ \n────\n⏵⏵ bypass permissions on";
        // Transcript/echoed prompt text quoting setup screens above a ready editor.
        Assert.Null(HerdrAgentControl.StartupBlocker(InteractiveAgentKind.Claude, "Not logged in · Please run /login\nChoose the theme\nDark mode" + editor));
        Assert.Equal("agent_login_required", HerdrAgentControl.StartupBlocker(InteractiveAgentKind.Claude, editor + "  Not logged in · Please run /login")?.Reason);
        // A resumed transcript can render before the editor.
        Assert.Null(HerdrAgentControl.StartupBlocker(InteractiveAgentKind.Claude, "Yes, I trust this folder\nSelect login method", resumed: true));
        Assert.Null(HerdrAgentControl.StartupBlocker(InteractiveAgentKind.Codex, "Sign in with ChatGPT\nAPI key", resumed: true));

        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr { BootstrapFromTab = true, Screen = "Choose the theme\nDark mode" };
        var control = new HerdrAgentControl(Terminal(fake), TimeSpan.FromMilliseconds(300));
        var launch = new InteractiveLaunch(InteractiveAgentKind.Claude, "atftest", state.Path, null, null, Path.Combine(state.Path, "bootstrap"))
        { LiveReuse = true };
        await control.StartAsync(launch, TestContext.Current.CancellationToken);
        var e = await Assert.ThrowsAsync<HerdrLaunchException>(() => control.PromptAsync(launch, "never sent", TestContext.Current.CancellationToken));
        Assert.Contains("not ready", e.Message);
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["session", "stop", ..]);
    }

    [Fact]
    public async Task Prompt_waits_past_the_readiness_bound_while_the_agent_is_busy_and_is_sent_once()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr { BootstrapFromTab = true, AgentStatuses = new Queue<string>([.. Enumerable.Repeat("working", 12), "idle"]) };
        var control = new HerdrAgentControl(Terminal(fake), TimeSpan.FromSeconds(2));
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null, Path.Combine(state.Path, "bootstrap"));
        await control.StartAsync(launch, TestContext.Current.CancellationToken);

        await control.PromptAsync(launch, "revive", TestContext.Current.CancellationToken);

        Assert.Empty(fake.AgentStatuses);
        Assert.Single(fake.Calls, c => c.Args is ["--session", _, "agent", "prompt", ..]);
    }

    [Fact]
    public async Task Cancelled_deferred_prompt_is_never_submitted()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr { BootstrapFromTab = true, AgentStatuses = new Queue<string>([.. Enumerable.Repeat("working", 40), "idle"]) };
        var control = new HerdrAgentControl(Terminal(fake), TimeSpan.FromSeconds(2));
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null, Path.Combine(state.Path, "bootstrap"));
        await control.StartAsync(launch, TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => control.PromptAsync(launch, "cancelled", cancel.Token));

        Assert.DoesNotContain(fake.Calls, c => c.Args is ["--session", _, "agent", "prompt", ..]);
    }

    [Fact]
    public async Task Blocked_startup_sends_no_prompt()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var fake = new FakeHerdr { BootstrapFromTab = true, AgentStatuses = new Queue<string>(["blocked"]) };
        var control = new HerdrAgentControl(Terminal(fake));
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null, Path.Combine(state.Path, "bootstrap"));
        await control.StartAsync(launch, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<HerdrLaunchException>(() => control.PromptAsync(launch, "never sent", TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fake.Calls, c => c.Args is ["--session", _, "agent", "prompt", ..]);
    }

    [Theory]
    [InlineData("agent_prompt_stalled", false)]
    [InlineData("timeout", false)]
    [InlineData(null, true)] // The CLI call itself exceeds the command deadline.
    public async Task UnsettledPrompt_IsSubmittedOnceAndEndsUncertain(string? code, bool processTimeout)
    {
        var state = Path.Combine("/tmp", "atf-herdr-turn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        try
        {
            var fake = new FakeHerdr
            {
                BootstrapFromTab = true,
                PromptResponse = processTimeout ? new CapturedProcess(true, -1, "", false, "", false)
                    : new CapturedProcess(false, 1, $$$"""{"error":{"code":"{{{code}}}"}}""", false, "", false),
            };
            var backend = new HerdrInteractiveBackend(new HerdrAgentControl(Terminal(fake)), new InteractiveTranscriptReader(name => name == "CODEX_HOME" ? state : null),
                InteractiveAgentKind.Codex, state, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(600));
            await using var run = backend.Start(new BackendRequest("job", "corr", "text", "") { WorkingDirectory = state });
            await run.DeliverAsync(CancellationToken.None);

            var evidence = new List<BackendEvidence>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var item in run.ReadEvidenceAsync(deadline.Token))
            {
                evidence.Add(item);
            }

            Assert.Single(fake.Calls, c => c.Args is ["--session", _, "agent", "prompt", ..]);
            Assert.DoesNotContain(evidence, e => e is BackendEvidence.Result);
            // Herdr's own wait timeout/stall may hide a delivered prompt: keep observing, then uncertain.
            // A CLI deadline is not proof either way: delivery is unconfirmed, never resent.
            Assert.DoesNotContain(evidence, e => e is BackendEvidence.Ack);
            Assert.Equal(new BackendEvidence.ProtocolError("interactive_delivery_not_confirmed"), evidence[^1]);
        }
        finally { Directory.Delete(state, recursive: true); }
    }

    /// <summary>Opt-in (ATF_HERDR_INTEGRATION=1): a dedicated atf-test-* server this test starts and stops.</summary>
    [Fact]
    public async Task RealHerdr_OwnedLaunchAndReplacement()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ATF_HERDR_INTEGRATION") == "1", "set ATF_HERDR_INTEGRATION=1 to run against the real herdr binary");
        var seed = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value, StringComparer.Ordinal);
        var terminal = new HerdrTerminal(new HerdrTerminalOptions { Environment = seed, SessionPrefix = "atf-test-" });
        var bootstrap = Path.Combine(Path.GetTempPath(), "atf-d7-" + Guid.NewGuid().ToString("N"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var session = await terminal.StartSessionAsync(cts.Token);
        try
        {
            var binding = await terminal.OpenAgentTabAsync(session, "agent-a", Path.GetTempPath(), bootstrap, cts.Token);
            Assert.Null(await terminal.VerifyBindingAsync(binding, cts.Token));

            await terminal.RunOwnedAsync(session, cts.Token, "tab", "close", binding.TabId);
            var replacement = await terminal.OpenAgentTabAsync(session, "agent-a", Path.GetTempPath(), bootstrap, cts.Token);

            Assert.NotNull(await terminal.VerifyBindingAsync(binding, cts.Token));
            Assert.Null(await terminal.VerifyBindingAsync(replacement, cts.Token));
        }
        finally
        {
            await terminal.StopOwnedSessionAsync(session, CancellationToken.None);
        }
    }

    [Fact]
    public async Task RealHerdr_DefaultSessionResolvesReadOnly()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ATF_HERDR_DEFAULT_READONLY") == "1", "read-only default-session check");
        var seed = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value, StringComparer.Ordinal);
        var terminal = new HerdrTerminal(new HerdrTerminalOptions { Environment = seed });
        var session = await terminal.ExistingSessionAsync("default", TestContext.Current.CancellationToken);
        Assert.True(session.Shared);
        Assert.EndsWith("/herdr.sock", session.SocketPath, StringComparison.Ordinal);
        Assert.True(session.ServerPid > 0);
    }

    [Fact]
    public async Task RealHerdr_TwoRepositoriesShareSessionWithTwoWorkspaces()
    {
        var name = Environment.GetEnvironmentVariable("ATF_HERDR_PLACEMENT_SMOKE_SESSION");
        Assert.SkipUnless(!string.IsNullOrEmpty(name), "set ATF_HERDR_PLACEMENT_SMOKE_SESSION to a scratch session started by this test operator");
        using var state = new TempStateDir();
        var seed = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value, StringComparer.Ordinal);
        var terminal = new HerdrTerminal(new HerdrTerminalOptions { Environment = seed });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var session = await terminal.ExistingSessionAsync(name!, cts.Token);
        var createdSessions = new List<OwnedHerdrSession>();
        var jobIds = new List<string>();
        try
        {
            foreach (var repoName in new[] { "placement-one", "placement-two" })
            {
                var repo = state.File(repoName);
                Directory.CreateDirectory(repo);
                var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("init");
                psi.ArgumentList.Add(repo);
                using var git = Process.Start(psi)!;
                Assert.True(git.WaitForExit(5000) && git.ExitCode == 0, git.StandardError.ReadToEnd());
                var bootstrap = state.File("herdr/" + repoName + ".bootstrap");
                Directory.CreateDirectory(Path.GetDirectoryName(bootstrap)!);
                File.WriteAllText(bootstrap, repoName);
                var jobId = "job-" + repoName;
                var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, repoName + "-agent", repo, null, null, bootstrap) { JobId = jobId };
                var binding = await terminal.OpenAgentTabAsync(session, launch.AgentName, repo, bootstrap, cts.Token,
                    onCreated: created => { createdSessions.Add(created); HerdrOwnedSessions.Save(launch, created); });
                jobIds.Add(jobId);
                Assert.Equal(session.SessionName, binding.Session.SessionName);
                Assert.Null(await terminal.VerifyBindingAsync(binding, cts.Token));
                Assert.Equal(session.SessionName, HerdrOwnedSessions.Location(state.Path, jobId)?.Session);
            }
            var listed = await terminal.RunOwnedAsync(session, cts.Token, "workspace", "list");
            var workspaces = (JsonArray)listed["result"]!["workspaces"]!;
            Assert.Equal(2, workspaces.Count);
            Assert.Equal(["placement-one", "placement-two"], workspaces.Select(w => w!["label"]!.GetValue<string>()).Order(StringComparer.Ordinal));
            Assert.True(new HerdrAgentControl(terminal).StopJobs(state.Path, jobIds));
            Assert.True((await terminal.ExistingSessionAsync(name!, cts.Token)).Shared);
        }
        finally
        {
            foreach (var created in createdSessions) { await terminal.StopOwnedSessionAsync(created, CancellationToken.None); }
        }
    }

    [Fact]
    public async Task RealHerdr_SharedTabLeavesSessionRunning()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ATF_HERDR_INTEGRATION") == "1", "isolated Herdr integration is opt-in");
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var home = state.File("home");
        Directory.CreateDirectory(home);
        var seed = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value, StringComparer.Ordinal);
        seed["HOME"] = home;
        var terminal = new HerdrTerminal(new HerdrTerminalOptions { Environment = seed, SessionPrefix = "atf-test-shared-" });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var owned = await terminal.StartSessionAsync(deadline.Token);
        try
        {
            var shared = await terminal.ExistingSessionAsync(owned.SessionName, deadline.Token);
            var bootstrap = state.File("bootstrap");
            var binding = await terminal.OpenAgentTabAsync(shared, "atf-live-test", state.Path, bootstrap, deadline.Token,
                onCreated: created => shared = created);
            var other = await terminal.ExistingSessionAsync(owned.SessionName, deadline.Token);
            var otherBinding = await terminal.OpenAgentTabAsync(other, "atf-live-other", state.Path, state.File("other-bootstrap"), deadline.Token,
                onCreated: created => other = created);
            Assert.Null(await terminal.VerifyBindingAsync(binding, deadline.Token));
            await terminal.StopOwnedSessionAsync(shared, deadline.Token);
            Assert.Null(await terminal.VerifyBindingAsync(otherBinding, deadline.Token));
            await terminal.StopOwnedSessionAsync(other, deadline.Token);
            Assert.NotNull(await terminal.ExistingSessionAsync(owned.SessionName, deadline.Token));
        }
        finally { await terminal.StopOwnedSessionAsync(owned, CancellationToken.None); }
    }

    private static ProcessStartInfo Sh(string script)
    {
        var psi = new ProcessStartInfo("sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        return psi;
    }

    public enum SpawnFault
    {
        None,
        SetsidFails,
        ServerNeverRuns,
        ForeignServerProcess,
        WorkspaceCreateFails,
    }

    public enum Replacement
    {
        NewTerminal,
        PaneGone,
        ServerRestarted,
        ShellReplaced,
    }

    private sealed record Call(string[] Args, IDictionary<string, string?> Env);

    /// <summary>Scripted Herdr CLI and /proc view; records every command and never runs a process.</summary>
    private sealed class FakeHerdr : IHerdrProcessRunner
    {
        public bool AgentStartFails { get; init; }
        /// <summary>How many `agent start` calls Herdr refuses as agent_pane_busy before the pane reads as a free shell.</summary>
        public int AgentStartBusy { get; set; }
        public bool PaneCloseFails { get; init; }
        public const string TakenName = "atf-test-taken";

        public SpawnFault Fault { get; init; }

        public bool Preexisting { get; init; }
        public bool SharedRunning { get; init; }

        /// <summary>When set, this many servers claim the default session.</summary>
        public int? DefaultServerCount { get; init; }
        bool _defaultStarted;
        int _listsSinceStart;

        /// <summary>After a start, the session lists as running only after this many further session lists.</summary>
        public int DefaultReadyAfterListCalls { get; init; }

        /// <summary>Another server (other HOME) also named default, next to the recorded one.</summary>
        public bool ForeignDefaultServer { get; set; }

        public string? PaneGetError { get; set; }
        public bool SharedWorkspaceInitiallyAbsent { get; init; }
        public bool UnreadableWorkspaceList { get; init; }
        public TimeSpan WorkspaceListDelay { get; init; }
        public string SharedWorkspaceLabel { get; init; } = "work";
        public bool TabReplaced { get; set; }
        public bool DefaultRunning { get; init; } = true;
        public string? ListedAgentName { get; set; }
        public string ListedAgentPane { get; set; } = "w1:p2";
        public string ListedAgentTab { get; set; } = "w1:t2";

        public bool Installed { get; init; } = true;

        public bool FloodSessionList { get; init; }

        public bool ShellParentIsServer { get; init; } = true;

        public string ShellBootstrap { get; init; } = Bootstrap;

        /// <summary>The shell carries whatever bootstrap its tab was created with.</summary>
        public bool BootstrapFromTab { get; init; }

        /// <summary>A macOS platform shell (/bin/zsh): its environment reads as absent.</summary>
        public bool HideShellEnvironment { get; init; }

        /// <summary>The platform answer: true on macOS, where a hidden environment is proven by the shell's child instead.</summary>
        public bool EnvironmentMayBeHidden { get; init; }

        /// <summary>The PID the child started by `pane run` reports for its parent shell; null when it never runs.</summary>
        public int? ShellProofReporter { get; set; }

        /// <summary>The pane's foreground process group; null is the shell itself (nothing running in it).</summary>
        public int? ForegroundProcessGroup { get; set; }

        public CapturedProcess? PromptResponse { get; init; }

        public List<Call> Calls { get; } = [];

        /// <summary>For reads racing a live dispatcher.</summary>
        public Call[] Snapshot() { lock (Calls) { return [.. Calls]; } }

        public List<ProcessStartInfo> Detached { get; } = [];

        public List<int> Killed { get; } = [];

        public int ServerPid { get; private set; } = 4100;

        public int ShellPid { get; private set; } = 4200;

        public string SocketPath => "/home/u/.config/herdr/sessions/" + _name + "/herdr.sock";

        string? _name;
        bool _running;
        ulong _serverStart = 77;
        ulong _shellStart = 88;
        string _terminal = "term_a";
        bool _paneGone;
        bool _workspaceCreated;

        public void Replace(Replacement replacement)
        {
            switch (replacement)
            {
                case Replacement.NewTerminal:
                    _terminal = "term_b";
                    break;
                case Replacement.PaneGone:
                    _paneGone = true;
                    break;
                case Replacement.ServerRestarted:
                    _serverStart = 99;
                    break;
                case Replacement.ShellReplaced:
                    _shellStart = 111;
                    break;
            }
        }

        public async Task<CapturedProcess> CaptureAsync(ProcessStartInfo psi, TimeSpan timeout, int maxStdoutBytes, int maxStderrBytes, CancellationToken cancellationToken)
        {
            string[] args = [.. psi.ArgumentList];
            lock (Calls) { Calls.Add(new(args, new Dictionary<string, string?>(psi.Environment))); }
            if (args is ["workspace", "list"] && WorkspaceListDelay > TimeSpan.Zero) { await Task.Delay(WorkspaceListDelay, cancellationToken); }
            return args switch
            {
                ["--version"] => Installed ? Ok("herdr 0.8.2") : new CapturedProcess(false, 127, "", false, "not found", false),
                ["session", "list", "--json"] => FloodSessionList ? new CapturedProcess(false, 0, "{", true, "", false) : Ok(SessionList()),
                ["workspace", "create", ..] => CreateWorkspace(),
                ["workspace", "list"] => WorkspaceList(),
                ["tab", "rename", ..] => Ok("{}"),
                ["tab", "create", ..] => Ok("""{"result":{"root_pane":{"pane_id":"w1:p2","tab_id":"w1:t2","terminal_id":"term_a"},"tab":{"tab_id":"w1:t2"}}}"""),
                ["tab", "get", "w1:t2"] => Ok(new JsonObject { ["result"] = new JsonObject { ["tab"] = new JsonObject { ["tab_id"] = "w1:t2", ["workspace_id"] = TabReplaced ? "other" : "w1" } } }.ToJsonString()),
                ["agent", "list"] => Ok(new JsonObject
                {
                    ["result"] = new JsonObject
                    {
                        ["agents"] = new JsonArray(
                    new JsonObject { ["pane_id"] = "w1:p9", ["tab_id"] = "w1:t9" },
                    new JsonObject { ["name"] = ListedAgentName, ["pane_id"] = ListedAgentPane, ["tab_id"] = ListedAgentTab })
                    }
                }.ToJsonString()),
                ["pane", "close", "w1:p2"] => PaneCloseFails ? Err("close_failed") : Ok("{}"),
                ["pane", "get", "w1:p2"] => PaneGetError is { } error ? Err(error) : _paneGone ? Err("pane_not_found") : Ok(new JsonObject { ["result"] = new JsonObject { ["pane"] = new JsonObject { ["pane_id"] = "w1:p2", ["tab_id"] = "w1:t2", ["terminal_id"] = _terminal } } }.ToJsonString()),
                ["pane", "process-info", "--pane", "w1:p2"] => Ok("""{"result":{"process_info":{"pane_id":"w1:p2","shell_pid":""" + ShellPid
                    + ""","foreground_process_group_id":""" + (ForegroundProcessGroup ?? ShellPid) + "}}}"),
                ["pane", "run", "w1:p2", HerdrTerminal.ShellProofCommand] => RunShellProof(),
                ["pane", "process-info", "--pane", "w1:p1"] => Ok("""{"result":{"process_info":{"pane_id":"w1:p1","shell_pid":""" + ShellPid + "}}}"),
                ["session", "stop" or "delete", ..] => Ok("{}"),
                ["agent", "start", ..] => AgentStartFails ? Err("agent_not_ready") : AgentStartBusy-- > 0 ? Err("agent_pane_busy") : Ok("{}"),
                ["agent", "read", ..] => Ok((_prompted && ScreenAfterPrompt is { } prompted ? prompted : Screen) ?? "› Ask Codex\n? for shortcuts\n❯ Try a task\nbypass permissions on\n──────\n──────\n/tmp/work"),
                ["agent", "get", ..] => Ok("{\"result\":{\"agent\":{\"status\":\"" + (AgentStatuses.TryDequeue(out var status) ? status : "idle") + "\"}}}"),
                ["--session", _, "agent", "prompt", ..] => (_prompted = true) && PromptResponse is { } response ? response : Ok("""{"result":{"type":"agent_prompted"}}"""),
                _ => Err("unexpected " + string.Join(' ', args)),
            };
        }

        CapturedProcess RunShellProof()
        {
            // The pane shell exports the tab's bootstrap; its child writes next to whatever it inherited.
            if (ShellProofReporter is { } reporter && TabBootstrap() is { } inherited)
            {
                File.WriteAllText(HerdrTerminal.ShellProofPath(inherited), reporter.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return Ok("{}");
        }

        CapturedProcess CreateWorkspace()
        {
            if (Fault == SpawnFault.WorkspaceCreateFails) { return Err("workspace_failed"); }
            _workspaceCreated = true;
            return Ok("""{"result":{"workspace":{"workspace_id":"w1","label":"x"},"root_pane":{"pane_id":"w1:p1","tab_id":"w1:t1","terminal_id":"term_a"},"tab":{"tab_id":"w1:t1"}}}""");
        }

        CapturedProcess WorkspaceList()
        {
            if (UnreadableWorkspaceList) { return Ok("""{"result":{"workspaces":"unreadable"}}"""); }
            JsonArray workspaces = SharedWorkspaceInitiallyAbsent && !_workspaceCreated ? []
                : [new JsonObject { ["workspace_id"] = "w1", ["label"] = _name is null && !_workspaceCreated ? SharedWorkspaceLabel : Label(), ["number"] = 1 }];
            return Ok(new JsonObject { ["result"] = new JsonObject { ["workspaces"] = workspaces } }.ToJsonString());
        }

        public string? Screen { get; init; }

        /// <summary>What the pane shows once a prompt was sent, e.g. the agent's own refusal.</summary>
        public string? ScreenAfterPrompt { get; init; }
        bool _prompted;

        public Queue<string> AgentStatuses { get; init; } = new();

        string Label() => Calls.Where(c => c.Args is ["workspace", "create", ..]).Select(c => c.Args[Array.IndexOf(c.Args, "--label") + 1]).LastOrDefault() ?? "";

        string SessionList()
        {
            var sessions = new JsonArray(new JsonObject { ["name"] = "default", ["running"] = DefaultRunning || _defaultStarted && Interlocked.Increment(ref _listsSinceStart) > DefaultReadyAfterListCalls, ["socket_path"] = "/home/u/.config/herdr/herdr.sock" });
            if (Preexisting)
            {
                sessions.Add((JsonNode)new JsonObject { ["name"] = TakenName, ["running"] = false });
            }
            if (_name is not null)
            {
                sessions.Add((JsonNode)new JsonObject { ["name"] = _name, ["running"] = _running, ["socket_path"] = SocketPath });
            }
            return new JsonObject { ["sessions"] = sessions }.ToJsonString();
        }

        public Task StartDetachedAsync(ProcessStartInfo psi, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Detached.Add(psi);
            if (Fault == SpawnFault.SetsidFails)
            {
                throw new System.ComponentModel.Win32Exception("setsid missing");
            }
            if (psi.ArgumentList[^1] == "default") { _defaultStarted = true; return Task.CompletedTask; }
            _name = psi.ArgumentList[^1];
            _running = Fault != SpawnFault.ServerNeverRuns;
            return Task.CompletedTask;
        }

        public IReadOnlyList<ProcessIdentity> FindServers(string sessionName, string? socketPath = null) =>
            sessionName == "default" && DefaultServerCount is { } count ? [.. Enumerable.Range(0, count).Select(i => new ProcessIdentity(ServerPid + i, _serverStart))] :
            sessionName == "default" && ForeignDefaultServer && (SharedRunning || _defaultStarted) ? [new(ServerPid, _serverStart), new(ServerPid + 1, 5)] :
            (SharedRunning || _defaultStarted) && sessionName == "default" ? [new(ServerPid, _serverStart)] :
            !_running || sessionName != _name ? []
            : Fault == SpawnFault.ForeignServerProcess ? [new(ServerPid, _serverStart), new(ServerPid + 1, 5)]
            : [new(ServerPid, _serverStart)];

        public ProcessIdentity? Identity(int pid) =>
            pid == ServerPid ? new(pid, _serverStart) : pid == ShellPid ? new(pid, _shellStart) : null;

        public int? ParentOf(int pid) => pid == ShellPid ? (ShellParentIsServer ? ServerPid : 1) : null;

        public string? EnvironmentValue(int pid, string name) =>
            pid == ShellPid && name == HerdrTerminal.BootstrapVariable && !HideShellEnvironment
                ? BootstrapFromTab ? TabBootstrap() : ShellBootstrap
                : null;

        string? TabBootstrap() => Calls.Where(c => c.Args is ["tab", "create", ..]).SelectMany(c => c.Args)
            .LastOrDefault(a => a.StartsWith(HerdrTerminal.BootstrapVariable + "=", StringComparison.Ordinal))?[(HerdrTerminal.BootstrapVariable.Length + 1)..];

        static CapturedProcess Ok(string stdout) => new(false, 0, stdout, false, "", false);

        static CapturedProcess Err(string code) => new(false, 1, $$$"""{"error":{"code":"{{{code}}}"}}""", false, "", false);
    }
}
