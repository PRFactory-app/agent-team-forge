using System.Text;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;

namespace AgentTeamForge.Tests.Features.Agents.Terminals;

public sealed class WtInteractiveBackendTests
{
    [Fact]
    public async Task FakeTabLaunchReportsNativeResultAndStopsOwnedTab()
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
        Assert.True(tabs.Stopped);
    }

    [Fact]
    public async Task FailedTabLaunchNeverReportsDelivery()
    {
        var tabs = new FakeTabs { FailLaunch = true };
        var backend = new WtInteractiveBackend(tabs, new FakeReader(null), InteractiveAgentKind.Claude, Path.GetTempPath());
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
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", "C:\\work's repo", "native-1", null, "C:\\state\\tab.ps1");
        var prompt = "first line; it's fine\nsecond — line";
        var wrapper = Encoding.UTF8.GetString(WtTabControl.WrapperBytes(launch, prompt, "C:\\state\\tab.pid"));
        Assert.StartsWith("\uFEFF", wrapper);
        Assert.Contains("& 'codex' '--dangerously-bypass-approvals-and-sandbox' '-C' 'C:\\work''s repo' 'resume' 'native-1'", wrapper);
        Assert.Contains("'first line; it''s fine\nsecond — line'", wrapper);
        Assert.Contains("$PID | Out-File", wrapper);
        Assert.EndsWith("exit 0\r\n", wrapper);
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
        public bool Stopped { get; private set; }
        public string Prompt { get; private set; } = "";
        public void Preflight(InteractiveAgentKind kind) => Preflighted = true;
        public Task StartAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken)
        {
            if (FailLaunch)
            {
                throw new IOException("tab failed");
            }

            Prompt = prompt;
            return Task.CompletedTask;
        }
        public bool IsAlive(InteractiveLaunch launch) => !Stopped;
        public int? ProcessId(InteractiveLaunch launch) => Prompt.Length > 0 ? 4242 : null;
        public void StopOwned(InteractiveLaunch launch) => Stopped = true;
    }

    sealed class FakeReader(InteractiveTranscript? transcript) : IInteractiveTranscriptReader
    {
        public InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started) => transcript;
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
