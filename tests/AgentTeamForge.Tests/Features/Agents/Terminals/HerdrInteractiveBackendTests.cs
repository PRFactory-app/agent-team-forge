using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;

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
    public void PiResumeUsesContinueInTheLocatedSessionDirectory()
    {
        var launch = new InteractiveLaunch(InteractiveAgentKind.Pi, "atftest", "/tmp", "native-pi", "/tmp/pi-one", "/tmp/bootstrap");
        var args = HerdrAgentControl.AgentArguments(launch);
        Assert.Contains("--continue", args);
        Assert.DoesNotContain("--session-id", args);
        Assert.Contains("/tmp/pi-one", args);
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
            ]);
            var reader = new InteractiveTranscriptReader();
            var launch = new InteractiveLaunch(InteractiveAgentKind.Pi, "atftest", root, null, dir, Path.Combine(root, "bootstrap"));
            Assert.Equal(dir, reader.FindPiSessionDirectory(Path.Combine(root, "pi-sessions"), "pi-native"));
            Assert.Equal(new InteractiveTranscript("pi-native", "new answer"), reader.Read(launch, "atf-corr:new-turn", DateTimeOffset.UtcNow));
            Assert.Null(reader.Read(launch, "atf-corr:other-turn", DateTimeOffset.UtcNow));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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
            return Task.CompletedTask;
        }

        public Task PromptAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken)
        {
            Prompt = prompt;
            return Task.CompletedTask;
        }

        public Task<InteractiveAgentStatus> StatusAsync(InteractiveLaunch launch, CancellationToken cancellationToken) =>
            Task.FromResult(InteractiveAgentStatus.Done);

        public void StopOwned(InteractiveLaunch launch) { }
    }

    sealed class FakeReader(InteractiveTranscript? output) : IInteractiveTranscriptReader
    {
        public InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started) => output;
        public string? FindPiSessionDirectory(string root, string sessionId) => "/tmp/pi-one";
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
    }
}
