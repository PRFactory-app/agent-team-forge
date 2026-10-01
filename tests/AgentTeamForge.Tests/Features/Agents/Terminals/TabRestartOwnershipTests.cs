using System.Diagnostics;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Recovery;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Terminals;

/// <summary>
/// Terminal tabs (Windows Terminal and macOS Terminal/kitty) across daemon shutdown, restart and stop: a tab whose
/// turn may still be running is never recorded or swept as idle, and only verified ownership stops or releases one.
/// </summary>
public sealed class TabRestartOwnershipTests
{
    const string Session = "native-session";

    [Fact]
    public async Task A_turn_still_running_at_shutdown_is_never_recorded_or_closed_as_idle()
    {
        var tabs = new RecordingTabs();
        using var backend = new WtInteractiveBackend(tabs, new Reader(new InteractiveTranscript(Session, "", Completed: false)),
            InteractiveAgentKind.Codex, Path.GetTempPath(), "terminal", idleTimeout: TimeSpan.Zero);
        var run = backend.Start(new BackendRequest("job-1", "corr", "work", "") { WorkingDirectory = Path.GetTempPath() });
        await run.DeliverAsync(CancellationToken.None);
        await foreach (var evidence in run.ReadEvidenceAsync(CancellationToken.None))
        {
            if (evidence is BackendEvidence.Session) { break; }
        }

        // The daemon shuts down mid-turn: the attempt's finally disposes the run.
        await run.DisposeAsync();

        Assert.Empty(tabs.Retained);
        Assert.False(backend.HasIdleSession(Session));
        backend.StopAllIdleSessions();
        Assert.Single(tabs.Live);
        Assert.True(backend.StopOwnedJob("job-1"));
        Assert.Empty(tabs.Live);
    }

    [Fact]
    public async Task Only_a_settled_turn_is_recorded_idle_and_a_native_turn_withdraws_that_record_until_it_settles()
    {
        var tabs = new RecordingTabs();
        using var backend = new WtInteractiveBackend(tabs, new Reader(new InteractiveTranscript(Session, "done", Completed: true)),
            InteractiveAgentKind.Claude, Path.GetTempPath(), "terminal");
        await using (var run = backend.Start(new BackendRequest("job-1", "corr", "work", "") { WorkingDirectory = Path.GetTempPath() }))
        {
            await run.DeliverAsync(CancellationToken.None);
            await foreach (var _ in run.ReadEvidenceAsync(CancellationToken.None)) { }
        }
        Assert.Equal([Session], tabs.Retained);

        Assert.True(backend.TakeIdleForNativeTurn(Session));
        Assert.Single(tabs.Busy);
        backend.RememberNativeTurn(Session);
        Assert.Equal([Session, Session], tabs.Retained);

        Assert.True(backend.TakeIdleForNativeTurn(Session));
        backend.ReleaseNativeTurn(Session);
        Assert.Equal(2, tabs.Busy.Count);
        Assert.Equal(3, tabs.Retained.Count);
        Assert.True(backend.HasIdleSession(Session));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Restart_keeps_a_native_claude_follow_up_fenced_while_its_session_tab_lives(bool mac, bool live)
    {
        using var f = new JobFixture();
        var tabs = new RecordingTabs();
        using var tab = new WtInteractiveBackend(tabs, new Reader(new InteractiveTranscript(Session, "done", Completed: true)),
            InteractiveAgentKind.Claude, Path.GetTempPath(), mac ? "terminal" : "wt");
        IJobBackend backend = mac ? new MacInteractiveBackend(tab) : tab;
        var catalog = new BackendCatalog().Register(BackendCatalog.Claude, () => backend);
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, f.TestProfile, f.Admission, catalog.Names);
        var parent = accept.Execute(new SubmitJobRequest("parent", "work", null, false) { Backend = BackendCatalog.Claude }).Job!;
        using (var first = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { }))
        {
            await first.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        }
        tabs.Last!.NativeTranscript = new(Session, "/fake/native.jsonl");
        Assert.True(tab.HasIdleClaudeSession(Session));
        var child = new FollowUpJob(f.Store, JobFixture.Operator, accept).Execute(new FollowUpRequest(parent.JobId, "next", "next")).Job!;
        using (var before = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { }))
        {
            Assert.Equal(child.JobId, before.TakeNativeClaude(parent.JobId, "/fake/home")!.Job.JobId);
        }

        // The daemon stops mid native turn; on macOS the tab's agent carries its launch marker, not this turn's.
        Assert.Contains(child.JobId, new RecoverOnStartup(f.Store).Execute());
        if (!live) { tabs.Live.Clear(); }
        var logs = new List<string>();
        using var restarted = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, line => { lock (logs) { logs.Add(line); } });
        restarted.RestoreAfterRestart(f.Store.RestartCandidates());
        using var lifetime = new CancellationTokenSource();
        var loop = restarted.RunAsync(lifetime.Token);
        try
        {
            await restarted.RestartResolved.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
            var recovered = f.Store.GetJob(child.JobId)!;
            if (live)
            {
                Assert.Equal(JobStatus.NeedsReconciliation, recovered.Status);
                Assert.True(f.Store.IsSessionFenced(child.JobId));
                // Held busy: no follow-up, idle close or retention cap can take or close it.
                Assert.False(tab.HasIdleSession(Session));
                tab.StopAllIdleSessions();
                Assert.Single(tabs.Live);
            }
            else
            {
                Assert.Equal(JobStatus.Failed, recovered.Status);
                Assert.Equal("daemon_restart_agent_gone", recovered.ReasonCode);
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            await loop.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_fenced_job_whose_tab_is_proven_gone_can_be_stopped_but_an_unverified_one_cannot()
    {
        using var f = new JobFixture();
        var job = f.Submit("tab-exited");
        var claim = f.Store.BeginNextAttempt()!;
        var tabs = new RecordingTabs();
        using var tab = new WtInteractiveBackend(tabs, new Reader(null), InteractiveAgentKind.Codex, Path.GetTempPath(), "terminal");
        await using (var run = tab.Start(new BackendRequest(job.JobId, claim.Correlation, "work", "") { WorkingDirectory = Path.GetTempPath() }))
        {
            await run.DeliverAsync(CancellationToken.None);
        }
        Assert.Equal([job.JobId], new RecoverOnStartup(f.Store).Execute());
        using var dispatcher = new DispatchJob(f.Store, new BackendCatalog().Register(BackendCatalog.Fake, () => new MacInteractiveBackend(tab)),
            f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        var stop = new StopJob(f.Store, JobFixture.Operator, dispatcher.CancelRunning, stopReconciled: dispatcher.StopReconciled);

        // The user closed the tab, but its agent's exit cannot be verified: ownership is kept.
        tabs.Live.Clear();
        Assert.Equal(JobErrors.OwnershipNotProven, stop.Execute(job.JobId).Error);
        Assert.True(f.Store.IsSessionFenced(job.JobId));

        tabs.Gone = true;
        Assert.Equal("stopped", stop.Execute(job.JobId).Outcome);
        Assert.False(f.Store.IsSessionFenced(job.JobId));
        Assert.Equal(1, tabs.Stops);
    }

    [Fact]
    public void Real_macos_tab_that_exited_is_proven_gone_and_its_records_are_removed_by_stop()
    {
        if (!OperatingSystem.IsMacOS()) { return; }
        using var state = new TempStateDir();
        var directory = Directory.CreateDirectory(Path.Combine(state.Path, "terminal")).FullName;
        File.WriteAllText(Path.Combine(directory, "atfgone.launch.job"), "job-gone");
        File.WriteAllText(Path.Combine(directory, "atfgone.launch.sh"), "prompt");
        File.WriteAllText(Path.Combine(directory, "atfunverified.launch.job"), "job-unverified");
        File.WriteAllText(Path.Combine(directory, "atfunverified.launch.pid"), "not a pid record");
        File.SetUnixFileMode(Path.Combine(directory, "atfunverified.launch.pid"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using (var exited = Process.Start(new ProcessStartInfo("/usr/bin/true") { UseShellExecute = false })!)
        {
            var token = DarwinProcess.CreationToken(exited.Id);
            exited.WaitForExit();
            WritePid(Path.Combine(directory, "atfgone.launch.pid"), exited.Id, token ?? 1);
        }
        using var backend = new WtInteractiveBackend(new MacTabControl("terminal", null, null), new Reader(null), InteractiveAgentKind.Codex, state.Path, "terminal");

        Assert.False(backend.OwnsLiveJob("job-gone"));
        Assert.True(backend.StopOwnedJob("job-gone"));
        Assert.Empty(Directory.EnumerateFiles(directory, "atfgone.*"));

        Assert.False(backend.StopOwnedJob("job-unverified"));
        Assert.True(File.Exists(Path.Combine(directory, "atfunverified.launch.job")));
    }

    [Fact]
    public void Stopping_a_macos_launch_before_it_reports_its_pid_keeps_an_already_open_wrapper_from_running_the_agent()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var state = new TempStateDir();
        var directory = Directory.CreateDirectory(Path.Combine(state.Path, "terminal")).FullName;
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atfstarting", state.Path, null, null, Path.Combine(directory, "atfstarting.launch.sh"))
        { JobId = "job-starting" };
        var sidecar = Path.ChangeExtension(launch.BootstrapPath, ".pid");
        var marker = state.File("agent-ran");
        // terminal-token's contract: create the PID sidecar only if it does not exist yet.
        var atf = state.File("atf");
        File.WriteAllText(atf, "#!/bin/sh\nset -C\n(umask 077; printf '%s 1' \"$3\" > \"$5\") 2>/dev/null || exit 1\n");
        File.SetUnixFileMode(atf, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var wrapper = MacTabControl.WrapperText(launch, "task", sidecar, atf);
        Assert.Contains("fi\nif [ ! -x ", wrapper);
        wrapper = wrapper.Replace("fi\nif [ ! -x ", "fi\ntouch " + MacTabControl.ShellQuote(marker) + "\nif [ ! -x ", StringComparison.Ordinal);
        File.WriteAllText(launch.BootstrapPath, wrapper);
        File.WriteAllText(Path.ChangeExtension(launch.BootstrapPath, ".job"), "job-starting");

        // Terminal's /bin/sh has already opened the wrapper when Stop arrives; the wrapper has not reported yet.
        new MacTabControl("terminal", null, null).StopOwned(launch);
        Assert.False(File.Exists(launch.BootstrapPath));
        Assert.False(File.Exists(Path.ChangeExtension(launch.BootstrapPath, ".job")));
        using (var shell = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", wrapper]) { UseShellExecute = false })!)
        {
            Assert.True(shell.WaitForExit(10_000));
        }

        Assert.False(File.Exists(marker));
        var control = new MacTabControl("terminal", null, null);
        Assert.Contains("terminal-token failed", control.StartFailure(launch));
        Assert.True(control.ProvenGone(launch));
        Assert.False(control.IsAlive(launch));
    }

    [Fact]
    public void Startup_recovery_tombstones_a_wrapper_that_never_reported_and_clears_it_once_proven_exited()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var state = new TempStateDir();
        var directory = Directory.CreateDirectory(Path.Combine(state.Path, "terminal")).FullName;
        var wrapper = Path.Combine(directory, "atflate.launch.sh");
        File.WriteAllText(wrapper, "prompt");
        File.WriteAllText(Path.ChangeExtension(wrapper, ".job"), "job-late");
        var logs = new List<string>();

        MacTabControl.Recover(state.Path, logs.Add);

        Assert.False(File.Exists(wrapper));
        Assert.False(File.Exists(Path.ChangeExtension(wrapper, ".job")));
        Assert.Equal("stopped", File.ReadAllText(Path.ChangeExtension(wrapper, ".pid")));
        Assert.Empty(logs);

        // The late tab ran its wrapper: terminal-token failed on the tombstone and recorded why.
        File.WriteAllText(Path.ChangeExtension(wrapper, ".start-error"), "atf terminal-token failed");
        MacTabControl.Recover(state.Path, logs.Add);
        Assert.Empty(Directory.EnumerateFiles(directory));
        Assert.Empty(logs);
    }

    [Fact]
    public void A_macos_tab_taken_by_a_native_turn_is_not_a_restart_survivor_until_it_is_idle_again()
    {
        if (!OperatingSystem.IsMacOS()) { return; }
        using var state = new TempStateDir();
        var directory = Directory.CreateDirectory(Path.Combine(state.Path, "terminal")).FullName;
        var launch = new InteractiveLaunch(InteractiveAgentKind.Claude, "atfbusy", state.Path, null, null, Path.Combine(directory, "atfbusy.launch.sh"));
        using var agent = Process.Start(new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false })!;
        try
        {
            WritePid(Path.ChangeExtension(launch.BootstrapPath, ".pid"), agent.Id, DarwinProcess.CreationToken(agent.Id)!.Value);
            var tabs = new MacTabControl("terminal", null, null);
            tabs.Retained(launch, Session);
            Assert.Single(MacTabControl.Survivors(state.Path, InteractiveAgentKind.Claude));

            tabs.Busy(launch);
            Assert.Empty(MacTabControl.Survivors(state.Path, InteractiveAgentKind.Claude));
            Assert.False(tabs.ProvenGone(launch));
            Assert.False(agent.HasExited);
        }
        finally
        {
            if (!agent.HasExited) { agent.Kill(); }
        }
    }

    static void WritePid(string path, int pid, ulong token)
    {
        File.WriteAllText(path, $"{pid} {token}");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    sealed class RecordingTabs : IWtTabControl
    {
        readonly Lock _gate = new();
        public List<InteractiveLaunch> Live { get; } = [];
        public List<string> Retained { get; } = [];
        public List<InteractiveLaunch> Busy { get; } = [];
        public InteractiveLaunch? Last { get; private set; }
        public int Stops { get; private set; }
        public bool Gone { get; set; }
        public void Preflight(InteractiveAgentKind kind) { }
        public Task StartAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken)
        {
            lock (_gate) { Last = launch; Live.Add(launch); }
            return Task.CompletedTask;
        }
        public bool IsAlive(InteractiveLaunch launch) { lock (_gate) { return Live.Any(live => live.AgentName == launch.AgentName); } }
        public string? StartFailure(InteractiveLaunch launch) => null;
        public bool WrapperExited(InteractiveLaunch launch) => false;
        public int? ProcessId(InteractiveLaunch launch) => null;
        public void StopOwned(InteractiveLaunch launch)
        {
            lock (_gate) { if (Live.RemoveAll(live => live.AgentName == launch.AgentName) > 0 || Gone) { Stops++; } }
        }
        void IWtTabControl.Retained(InteractiveLaunch launch, string sessionId) { lock (_gate) { Retained.Add(sessionId); } }
        void IWtTabControl.Busy(InteractiveLaunch launch) { lock (_gate) { Busy.Add(launch); } }
        public bool ProvenGone(InteractiveLaunch launch) => Gone && !IsAlive(launch);
    }

    sealed class Reader(InteractiveTranscript? transcript) : IInteractiveTranscriptReader
    {
        public InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started) => transcript;
        public string? FindPiSessionDirectory(string root, string sessionId) => null;
    }
}
