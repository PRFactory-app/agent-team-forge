using System.Text;
using System.Text.Json.Nodes;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Terminals;

public sealed class WtInteractiveBackendTests
{
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
    public async Task WrapperStartFailureFailsJobWithoutFence()
    {
        using var f = new JobFixture();
        var job = f.Submit("wt-failed-start");
        var claim = f.Store.BeginNextAttempt()!;
        var tabs = new FakeTabs { Failure = "The specified executable is not a valid application" };
        var backend = new WtInteractiveBackend(tabs, new FakeReader(null), InteractiveAgentKind.Claude,
            Path.GetTempPath(), "wt", TimeSpan.FromSeconds(2));
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });

        await dispatcher.RunAttemptAsync(claim, CancellationToken.None);

        var record = f.Store.GetJob(job.JobId)!;
        Assert.Equal(JobStatus.Failed, record.Status);
        Assert.Equal("launch_failed", record.ReasonCode);
        Assert.Contains("not a valid application", record.ResultText);
        Assert.False(f.Store.IsSessionFenced(job.JobId));
        Assert.True(tabs.Stopped);
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
    public void RecoveryStopsOnlyTabsWhosePidStillHasTheRecordedIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "atf-wt-recovery-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "wt");
        Directory.CreateDirectory(directory);
        var created = new DateTime(638945424000000000, DateTimeKind.Utc);
        try
        {
            File.WriteAllText(Path.Combine(directory, "atfvalid.pid"), $"4242|{created.Ticks}");
            File.WriteAllText(Path.Combine(directory, "atfstale.pid"), $"5252|{created.Ticks}");
            var stopped = new List<int>();
            var count = WtTabControl.RecoverOwned(root,
                pid => pid == 4242 ? created : created.AddSeconds(1),
                tab => { stopped.Add(tab.Pid); return true; });
            Assert.Equal(1, count);
            Assert.Equal([4242], stopped);
        }
        finally { Directory.Delete(root, recursive: true); }
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

    sealed class FakeTabs : IWtTabControl
    {
        public bool Preflighted { get; private set; }
        public bool FailLaunch { get; init; }
        public bool FailPreflight { get; init; }
        public string? Failure { get; init; }
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

            Prompt = prompt;
            Launch = launch;
            return Task.CompletedTask;
        }
        public bool IsAlive(InteractiveLaunch launch) => !Stopped;
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

    sealed class SequenceReader(params InteractiveTranscript[] snapshots) : IInteractiveTranscriptReader
    {
        readonly Queue<InteractiveTranscript> _remaining = new(snapshots);
        public InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started) =>
            _remaining.Count > 0 ? _remaining.Dequeue() : snapshots[^1];
        public string? FindPiSessionDirectory(string root, string sessionId) => null;
    }
}
