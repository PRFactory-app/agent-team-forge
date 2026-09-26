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

    private static HerdrTerminal Terminal(FakeHerdr fake, IReadOnlyDictionary<string, string?>? env = null) =>
        new(new HerdrTerminalOptions
        {
            Environment = env ?? Desktop,
            SessionPrefix = "atf-test-",
            StartupTimeout = TimeSpan.FromMilliseconds(400),
            PollInterval = TimeSpan.FromMilliseconds(10),
            NewSessionName = fake.Preexisting ? () => FakeHerdr.TakenName : null,
        }, fake);

    [Fact]
    public async Task OwnedLaunch_RetainsHandleAndBootstrapProof()
    {
        var fake = new FakeHerdr();
        var terminal = Terminal(fake);

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
        Assert.Equal(["-f", "sh", "-c", HerdrCommands.ServerScript, session.SessionName], server.ArgumentList);
        var tab = fake.Calls.Single(c => c.Args is ["tab", "create", ..]);
        Assert.Equal(["tab", "create", "--workspace", "w1", "--cwd", "/work", "--label", "agent-a", "--env", "ATF_BOOTSTRAP_FILE=" + Bootstrap, "--no-focus"], tab.Args);
        foreach (var env in fake.Calls.Select(c => c.Env).Append(server.Environment))
        {
            Assert.DoesNotContain(env.Values, v => v?.Contains(Sentinel, StringComparison.Ordinal) == true);
            Assert.False(env.TryGetValue("HERDR_SOCKET_PATH", out var socket) && socket == "/home/u/.config/herdr/herdr.sock");
        }
        Assert.All(fake.Calls.Where(c => c.Args is ["workspace" or "tab" or "pane", ..]), c => Assert.Equal(fake.SocketPath, c.Env["HERDR_SOCKET_PATH"]));
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

        var error = await Assert.ThrowsAsync<InteractiveTerminalUnavailableException>(() => Terminal(fake, env).StartSessionAsync(CancellationToken.None));

        Assert.NotEmpty(error.Message);
        Assert.Empty(fake.Detached);
        Assert.All(fake.Calls, c => Assert.Equal(["--version"], c.Args));
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
                InteractiveAgentKind.Codex, state, TimeSpan.FromMilliseconds(300));
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
                InteractiveAgentKind.Codex, state, TimeSpan.FromMilliseconds(500));
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
            Assert.Equal(new BackendEvidence.ProtocolError(processTimeout ? "interactive_delivery_not_confirmed" : "interactive_completion_unobserved"), evidence[^1]);
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
        public const string TakenName = "atf-test-taken";

        public SpawnFault Fault { get; init; }

        public bool Preexisting { get; init; }

        public bool Installed { get; init; } = true;

        public bool FloodSessionList { get; init; }

        public bool ShellParentIsServer { get; init; } = true;

        public string ShellBootstrap { get; init; } = Bootstrap;

        /// <summary>The shell carries whatever bootstrap its tab was created with.</summary>
        public bool BootstrapFromTab { get; init; }

        public CapturedProcess? PromptResponse { get; init; }

        public List<Call> Calls { get; } = [];

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

        public Task<CapturedProcess> CaptureAsync(ProcessStartInfo psi, TimeSpan timeout, int maxStdoutBytes, int maxStderrBytes, CancellationToken cancellationToken)
        {
            string[] args = [.. psi.ArgumentList];
            Calls.Add(new(args, new Dictionary<string, string?>(psi.Environment)));
            return Task.FromResult(args switch
            {
                ["--version"] => Installed ? Ok("herdr 0.8.2") : new CapturedProcess(false, 127, "", false, "not found", false),
                ["session", "list", "--json"] => FloodSessionList ? new CapturedProcess(false, 0, "{", true, "", false) : Ok(SessionList()),
                ["workspace", "create", ..] => Fault == SpawnFault.WorkspaceCreateFails ? Err("workspace_failed") : Ok("""{"result":{"workspace":{"workspace_id":"w1","label":"x"}}}"""),
                ["workspace", "list"] => Ok(new JsonObject { ["result"] = new JsonObject { ["workspaces"] = new JsonArray(new JsonObject { ["workspace_id"] = "w1", ["label"] = Label(), }) } }.ToJsonString()),
                ["tab", "create", ..] => Ok("""{"result":{"root_pane":{"pane_id":"w1:p2","tab_id":"w1:t2","terminal_id":"term_a"},"tab":{"tab_id":"w1:t2"}}}"""),
                ["pane", "get", "w1:p2"] => _paneGone ? Err("pane_not_found") : Ok(new JsonObject { ["result"] = new JsonObject { ["pane"] = new JsonObject { ["pane_id"] = "w1:p2", ["tab_id"] = "w1:t2", ["terminal_id"] = _terminal } } }.ToJsonString()),
                ["pane", "process-info", "--pane", "w1:p2"] => Ok("""{"result":{"process_info":{"pane_id":"w1:p2","shell_pid":""" + ShellPid + "}}}"),
                ["session", "stop" or "delete", ..] => Ok("{}"),
                ["agent", "start", ..] => Ok("{}"),
                ["agent", "get", ..] => Ok("""{"result":{"agent":{"status":"idle"}}}"""),
                ["--session", _, "agent", "prompt", ..] => PromptResponse ?? Ok("""{"result":{"type":"agent_prompted"}}"""),
                _ => Err("unexpected " + string.Join(' ', args)),
            });
        }

        string Label() => Calls.Where(c => c.Args is ["workspace", "create", ..]).Select(c => c.Args[Array.IndexOf(c.Args, "--label") + 1]).LastOrDefault() ?? "";

        string SessionList()
        {
            var sessions = new JsonArray(new JsonObject { ["name"] = "default", ["running"] = true, ["socket_path"] = "/home/u/.config/herdr/herdr.sock" });
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
            _name = psi.ArgumentList[^1];
            _running = Fault != SpawnFault.ServerNeverRuns;
            return Task.CompletedTask;
        }

        public IReadOnlyList<ProcessIdentity> FindServers(string sessionName) =>
            !_running || sessionName != _name ? []
            : Fault == SpawnFault.ForeignServerProcess ? [new(ServerPid, _serverStart), new(ServerPid + 1, 5)]
            : [new(ServerPid, _serverStart)];

        public ProcessIdentity? Identity(int pid) =>
            pid == ServerPid ? new(pid, _serverStart) : pid == ShellPid ? new(pid, _shellStart) : null;

        public int? ParentOf(int pid) => pid == ShellPid ? (ShellParentIsServer ? ServerPid : 1) : null;

        public string? EnvironmentValue(int pid, string name) =>
            pid == ShellPid && name == HerdrTerminal.BootstrapVariable
                ? BootstrapFromTab ? TabBootstrap() : ShellBootstrap
                : null;

        string? TabBootstrap() => Calls.Where(c => c.Args is ["tab", "create", ..]).SelectMany(c => c.Args)
            .LastOrDefault(a => a.StartsWith(HerdrTerminal.BootstrapVariable + "=", StringComparison.Ordinal))?[(HerdrTerminal.BootstrapVariable.Length + 1)..];

        static CapturedProcess Ok(string stdout) => new(false, 0, stdout, false, "", false);

        static CapturedProcess Err(string code) => new(false, 1, $$$"""{"error":{"code":"{{{code}}}"}}""", false, "", false);
    }
}
