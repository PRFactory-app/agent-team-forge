using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Recovery;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Terminals;

/// <summary>
/// The dispatcher treats a macOS terminal backend, which wraps the tab backend rather than being one,
/// exactly like Windows tabs: interactive runtime bound, kept across daemon shutdown and restart, and
/// stopped through its owned tab.
/// </summary>
public sealed class MacInteractiveDispatchTests
{
    static MacInteractiveBackend Backend(HeldTabs tabs, IInteractiveTranscriptReader reader) =>
        new(new WtInteractiveBackend(tabs, reader, InteractiveAgentKind.Codex, Path.GetTempPath(), "terminal"));

    static DispatchJob Dispatcher(JobFixture f, IJobBackend backend) =>
        new(f.Store, new BackendCatalog().Register(BackendCatalog.Fake, () => backend), f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });

    [Fact]
    public async Task Interactive_turn_gets_the_interactive_runtime_bound_not_the_fake_backend_one()
    {
        using var f = new JobFixture(new SpikeLimits { MaxFakeRuntime = TimeSpan.FromMilliseconds(200) });
        var job = f.Submit("mac-runtime");
        var claim = f.Store.BeginNextAttempt()!;
        var backend = Backend(new HeldTabs(), new LateReader(TimeSpan.FromMilliseconds(1500)));
        using var dispatcher = Dispatcher(f, backend);

        await dispatcher.RunAttemptAsync(claim, CancellationToken.None).WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Completed, f.Store.GetJob(job.JobId)!.Status);
    }

    [Fact]
    public async Task Daemon_shutdown_keeps_the_running_tab()
    {
        using var f = new JobFixture();
        f.Submit("mac-shutdown");
        var claim = f.Store.BeginNextAttempt()!;
        var tabs = new HeldTabs();
        using var dispatcher = Dispatcher(f, Backend(tabs, new LateReader(Timeout.InfiniteTimeSpan)));
        using var lifetime = new CancellationTokenSource();

        var attempt = dispatcher.RunAttemptAsync(claim, lifetime.Token);
        await Bounded.Until(() => tabs.Started, "tab launch");
        await lifetime.CancelAsync();
        await attempt.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);

        Assert.False(tabs.Stopped);
    }

    [Fact]
    public async Task Restart_keeps_a_live_tab_job_fenced_and_stop_job_closes_that_tab()
    {
        using var f = new JobFixture();
        var job = f.Submit("mac-restart");
        var claim = f.Store.BeginNextAttempt()!;
        var tabs = new HeldTabs();
        var backend = Backend(tabs, new LateReader(Timeout.InfiniteTimeSpan));
        await using (var run = backend.Start(new BackendRequest(job.JobId, claim.Correlation, "work", "") { WorkingDirectory = Path.GetTempPath() }))
        {
            await run.DeliverAsync(CancellationToken.None);
        }
        Assert.Equal([job.JobId], new RecoverOnStartup(f.Store).Execute());
        using var dispatcher = Dispatcher(f, backend);
        dispatcher.RestoreAfterRestart(f.Store.RestartCandidates());
        using var lifetime = new CancellationTokenSource();
        var loop = dispatcher.RunAsync(lifetime.Token);
        try
        {
            await dispatcher.RestartResolved.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
            Assert.Equal(JobStatus.NeedsReconciliation, f.Store.GetJob(job.JobId)!.Status);
            Assert.True(f.Store.IsSessionFenced(job.JobId));

            var stopped = new StopJob(f.Store, JobFixture.Operator, dispatcher.CancelRunning, stopReconciled: dispatcher.StopReconciled).Execute(job.JobId);
            Assert.Equal("stopped", stopped.Outcome);
            Assert.True(tabs.Stopped);
            Assert.False(f.Store.IsSessionFenced(job.JobId));
        }
        finally
        {
            await lifetime.CancelAsync();
            await loop.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void A_restarted_daemon_finds_the_macos_tab_of_a_job_by_its_record()
    {
        using var state = new TempStateDir();
        var directory = Directory.CreateDirectory(Path.Combine(state.Path, "terminal")).FullName;
        File.WriteAllText(Path.Combine(directory, "atfforeign.launch.job"), "other-job");
        File.WriteAllText(Path.Combine(directory, "atfowned.launch.job"), "our-job");

        var found = WtInteractiveBackend.FindRecoveredLaunch(state.Path, InteractiveAgentKind.Claude, "our-job", "terminal");

        Assert.Equal(("atfowned", Path.Combine(directory, "atfowned.launch.sh")), (found?.AgentName, found?.BootstrapPath));
        Assert.Null(WtInteractiveBackend.FindRecoveredLaunch(state.Path, InteractiveAgentKind.Claude, "missing-job", "terminal"));
    }

    sealed class HeldTabs : IWtTabControl
    {
        volatile bool _started;
        volatile bool _stopped;
        public bool Started => _started;
        public bool Stopped => _stopped;
        public void Preflight(InteractiveAgentKind kind) { }
        public Task StartAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken)
        {
            _started = true;
            return Task.CompletedTask;
        }
        public bool IsAlive(InteractiveLaunch launch) => _started && !_stopped;
        public string? StartFailure(InteractiveLaunch launch) => null;
        public bool WrapperExited(InteractiveLaunch launch) => false;
        public int? ProcessId(InteractiveLaunch launch) => _started ? 4242 : null;
        public void StopOwned(InteractiveLaunch launch) => _stopped = true;
    }

    /// <summary>The turn's transcript completes only after <paramref name="delay"/>; infinite never completes.</summary>
    sealed class LateReader(TimeSpan delay) : IInteractiveTranscriptReader
    {
        readonly DateTimeOffset _since = DateTimeOffset.UtcNow;
        public InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started) =>
            delay != Timeout.InfiniteTimeSpan && DateTimeOffset.UtcNow - _since >= delay
                ? new InteractiveTranscript("native-session", "done", Completed: true) : null;
        public string? FindPiSessionDirectory(string root, string sessionId) => null;
    }
}
