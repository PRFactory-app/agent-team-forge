using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

public enum InteractiveAgentKind { Claude, Codex, Pi }

public enum PaneOwnerRecovery { Unverified, Retained, Released, Gone, GoneAgain }

/// <summary>Runs a real agent TUI in a tab of an ATF-owned Herdr session.</summary>
public sealed class HerdrInteractiveBackend : IJobBackend, IInteractiveSessionStop, IDisposable
{
    readonly IHerdrAgentControl _control;
    readonly IInteractiveTranscriptReader _transcripts;
    readonly InteractiveAgentKind _kind;
    readonly string _stateRoot;
    readonly RetainedSessions _liveSessions;
    readonly ConcurrentDictionary<string, InteractiveLaunch> _nativeSessions = new();
    internal Lock SessionStopGate { get; } = new();
    readonly TimeSpan _settleTimeout;
    readonly TimeSpan _startupTimeout;

    public HerdrInteractiveBackend(HerdrTerminal terminal, InteractiveAgentKind kind, string stateRoot, TimeSpan? idleTimeout = null, Func<InteractiveRetentionSettings>? retentionSettings = null)
        : this(new HerdrAgentControl(terminal), new InteractiveTranscriptReader(terminal.Env), kind, stateRoot, idleTimeout: idleTimeout, retentionSettings: retentionSettings) { }

    // settleTimeout: how long an idle agent may go without native completion before the turn is uncertain.
    internal HerdrInteractiveBackend(IHerdrAgentControl control, IInteractiveTranscriptReader transcripts, InteractiveAgentKind kind, string stateRoot,
        TimeSpan? settleTimeout = null, TimeSpan? startupTimeout = null, TimeSpan? idleTimeout = null, TimeProvider? timeProvider = null, Func<InteractiveRetentionSettings>? retentionSettings = null)
    {
        _control = control;
        _transcripts = transcripts;
        _kind = kind;
        _stateRoot = stateRoot;
        _liveSessions = new RetainedSessions(control.StopOwned, idleTimeout, timeProvider, retentionSettings, isIdle: PaneIsIdleAsync);
        _settleTimeout = settleTimeout ?? TimeSpan.FromSeconds(60);
        _startupTimeout = startupTimeout ?? InteractiveStartup.Timeout;
    }

    public void Dispose() => _liveSessions.Dispose();

    public IBackendRun Start(BackendRequest request)
    {
        var cwd = request.WorkingDirectory ?? Environment.CurrentDirectory;
        if (!Path.IsPathFullyQualified(cwd) || !Directory.Exists(cwd))
        {
            throw new BackendNotStartedException("interactive working directory does not exist");
        }

        var started = DateTimeOffset.UtcNow;
        if (request.ResumeSessionId is { } resumeId && RetainedTabMayBeLive(resumeId) && _liveSessions.TryTake(resumeId, out var live))
        {
            var (model, effort) = InteractiveLaunch.Selection(request.Options);
            if (live.Model == model && live.Effort == effort)
            {
                var reused = live with { StartupProgress = request.StartupProgress, LiveReuse = true, JobId = request.JobId };
                if (_control is HerdrAgentControl control) { control.TransferOwnership(reused); }
                return new Run(_control, _transcripts, request, reused, started, _settleTimeout, _startupTimeout, RememberSession, BindNativeSession, StopLaunch);
            }
            StopLaunch(live);
        }

        if (request.ResumeSessionId is { } resumed) { RetireReleasedPane(resumed); }
        var agentName = "atf" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10));
        var piDirectory = _kind == InteractiveAgentKind.Pi ? PiDirectory(request) : null;
        var launch = new InteractiveLaunch(_kind, agentName, cwd, request.ResumeSessionId, piDirectory,
            Path.Combine(_stateRoot, "herdr", agentName + ".bootstrap"))
        {
            StartupProgress = request.StartupProgress,
            JobId = request.JobId,
            TabLabel = TabLabel(_kind, request.DisplayName, request.JobId),
            HerdrPlacement = AgentTeamForge.Business.Features.Jobs.JobOptions.Read(request.Options, "herdr_placement")
        }.WithSelection(request.Options);
        try
        {
            // Dispatch calls Start on a worker. A failure after session creation is uncertain;
            // only the no-effect preflight above may be reported as BackendNotStarted.
            _control.StartAsync(launch, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is HerdrLaunchException or InteractiveTerminalUnavailableException)
        {
            throw new InvalidOperationException("interactive agent launch is uncertain: " + e.Message, e);
        }
        return new Run(_control, _transcripts, request, launch, started, _settleTimeout, _startupTimeout, RememberSession, BindNativeSession, StopLaunch);
    }

    /// <summary>
    /// Observe a prompt already submitted before daemon death; never send it again.
    /// <paramref name="gone"/> is true only when no turn can still be running there
    /// (prompt never submitted, no owned pane, or its server/shell process is proven gone);
    /// any other failure to rebind leaves the job fenced.
    /// </summary>
    public IBackendRun? Reattach(JobRecord job, RunRecord run, out bool gone)
    {
        gone = false;
        if (_control is not HerdrAgentControl control || !DateTimeOffset.TryParse(run.StartedAt, out var started)) { return null; }
        var owned = HerdrOwnedSessions.Read(_stateRoot, _ => { })
            .FirstOrDefault(entry => entry.Session.JobId == job.JobId);
        if (run.SubmittedAt is null || owned.Session is null) { gone = true; return null; }
        gone = control.PaneIsGone(owned.Session);
        if (gone) { return null; }
        var launch = Rebind(control, owned.Path, owned.Session, job, job.SessionId);
        if (launch is null)
        {
            gone = control.PaneIsGone(owned.Session);
            return null;
        }
        var request = new BackendRequest(job.JobId, run.Correlation, job.Instruction, job.Options)
        {
            DisplayName = job.TargetAgent,
            ResumeSessionId = job.SessionId,
            WorkingDirectory = JobWorktree.WorkingDirectory(job),
        };
        return new Run(_control, _transcripts, request, launch, started, _settleTimeout, _startupTimeout,
            RememberSession, BindNativeSession, StopLaunch, recovered: true);
    }

    /// <summary>
    /// Rebind the pane a native turn was delivered into. Its ownership record belongs to the
    /// session peer that launched the pane, never to the native follow-up. Returns that owner's
    /// job id and keeps the session reserved until native settlement; <paramref name="gone"/> as for Reattach.
    /// </summary>
    public string? ReattachNativeTurn(string sessionId, IReadOnlyList<JobRecord> sessionPeers, out bool gone)
    {
        gone = false;
        if (_control is not HerdrAgentControl control || _kind == InteractiveAgentKind.Pi) { return null; }
        var owned = HerdrOwnedSessions.Read(_stateRoot, _ => { })
            .Select(entry => (entry.Path, entry.Session, Owner: sessionPeers.FirstOrDefault(peer => peer.JobId == entry.Session.JobId)))
            .Where(entry => entry.Owner is not null).ToList();
        foreach (var (path, session, owner) in owned)
        {
            if (control.PaneIsGone(session) || Rebind(control, path, session, owner!, sessionId) is not { } launch
                || !BindTranscript(launch, session, sessionId)) { continue; }
            BindNativeSession(sessionId, launch);
            _liveSessions.TakeForNativeTurn(sessionId, running: true);
            return owner!.JobId;
        }
        gone = owned.All(entry => control.PaneIsGone(entry.Session));
        return null;
    }

    /// <summary>
    /// Restart fences every job with an ownership record, including a terminal job whose idle pane
    /// was retained for its next turn. That pane is retained again only with verified pane identity,
    /// the job's exact native session live in the pane, and transcript evidence that the newest turn
    /// there is an ATF turn (one of <paramref name="correlations"/>) that completed with no
    /// pending background work. <paramref name="releaseFence"/> revalidates the session and clears the
    /// fence before the pane is offered. A proven-gone pane, or a verified idle Pi pane, only releases the fence:
    /// nothing is started or closed. Serialized with stop_agent, which must never see its fence cleared.
    /// </summary>
    public PaneOwnerRecovery RecoverTerminalOwner(JobRecord owner, IReadOnlyList<string> correlations, Func<bool> releaseFence)
    {
        if (_control is not HerdrAgentControl control) { return PaneOwnerRecovery.Unverified; }
        lock (SessionStopGate)
        {
            var owned = HerdrOwnedSessions.Read(_stateRoot, _ => { }).FirstOrDefault(entry => entry.Session.JobId == owner.JobId);
            if (owned.Session is null) { return PaneOwnerRecovery.Unverified; }
            if (control.PaneIsGone(owned.Session)) { return Gone(owned.Path, owned.Session, releaseFence); }
            if (owner.SessionId is not { } sessionId || Rebind(control, owned.Path, owned.Session, owner, sessionId) is not { } launch)
            {
                return control.PaneIsGone(owned.Session) ? Gone(owned.Path, owned.Session, releaseFence) : PaneOwnerRecovery.Unverified;
            }
            // Pi's live session cannot be proven, so its pane is never typed into again. A verified idle pane
            // only releases the fence; the next follow-up retires it before resuming the session in a new tab.
            if (_kind == InteractiveAgentKind.Pi)
            {
                if (!SettledIdle(launch) || !releaseFence()) { return PaneOwnerRecovery.Unverified; }
                _releasedPanes[sessionId] = (launch, owned.Session);
                return PaneOwnerRecovery.Released;
            }
            if (!BindTranscript(launch, owned.Session, sessionId) || !SettledIdle(launch) || !LatestTurnSettled(launch, correlations)
                || !releaseFence()) { return PaneOwnerRecovery.Unverified; }
            BindNativeSession(sessionId, launch);
            _liveSessions.Remember(sessionId, launch);
            return PaneOwnerRecovery.Retained;
        }
    }

    // Panes whose restart fence was released without retention, by native session. Their agent can still write
    // the session, so a resume must close them first.
    readonly ConcurrentDictionary<string, (InteractiveLaunch Launch, OwnedHerdrSession Pane)> _releasedPanes = new();

    // A busy pane (an operator typed into it) or one not proven closed refuses the resume before anything starts.
    void RetireReleasedPane(string sessionId)
    {
        lock (SessionStopGate)
        {
            if (!_releasedPanes.TryGetValue(sessionId, out var released) || _control is not HerdrAgentControl control) { return; }
            if (!control.PaneIsGone(released.Pane))
            {
                if (!SettledIdle(released.Launch)) { throw new BackendNotStartedException("the agent's earlier pane is busy; let it finish or stop the agent, then retry"); }
                StopLaunch(released.Launch);
                // StopOwned deletes the ownership record only once Herdr confirmed the close.
                if (File.Exists(HerdrOwnedSessions.PathFor(released.Launch))) { throw new BackendNotStartedException("the agent's earlier pane could not be closed; stop the agent, then retry"); }
            }
            _releasedPanes.TryRemove(sessionId, out _);
        }
    }

    // The record stays as proof for the restored-pane sweep and stop_agent. A sibling marker makes
    // later starts repeat the release silently instead of logging it again. The bootstrap files only
    // proved the pane, which is gone, and processes its agent left behind no longer belong to anything.
    static PaneOwnerRecovery Gone(string recordPath, OwnedHerdrSession session, Func<bool> releaseFence)
    {
        if (!releaseFence()) { return PaneOwnerRecovery.Unverified; }
        if (HerdrOwnedSessions.ValidAgentName(session.AgentName)) { OrphanedBackendProcess.TerminateMarked([session.AgentName!]); }
        HerdrOwnedSessions.DeleteLaunchFiles(HerdrOwnedSessions.BootstrapForRecord(recordPath));
        var marker = recordPath + ".gone";
        if (File.Exists(marker)) { return PaneOwnerRecovery.GoneAgain; }
        try { File.WriteAllText(marker, "released"); }
        catch (IOException) { } // Best effort: the next start logs again.
        return PaneOwnerRecovery.Gone;
    }

    // The newest ATF turn in the transcript must also be its latest native turn (no later human input,
    // no unreadable tail), completed with no pending background work. Missing or ambiguous evidence keeps the fence.
    bool LatestTurnSettled(InteractiveLaunch launch, IReadOnlyCollection<string> correlations) =>
        _transcripts.ReadLatestTurn(launch, correlations)
            is { Completed: true, Superseded: false, Incomplete: false, PendingBackgroundTasks: false, ApiError: null, BindingError: null };

    /// <summary>Tests replace the /proc probe; production asks the transcript reader.</summary>
    internal Func<InteractiveLaunch, int, IReadOnlySet<string>?>? LiveSessionProbe { get; init; }

    // One launch per durable pane record, so every job recovered into a pane shares its retention identity.
    readonly ConcurrentDictionary<string, InteractiveLaunch> _rebound = new();

    InteractiveLaunch? Rebind(HerdrAgentControl control, string recordPath, OwnedHerdrSession session, JobRecord owner, string? sessionId)
    {
        if (_rebound.TryGetValue(recordPath, out var bound) && control.IsBound(bound)) { return bound; }
        var bootstrap = HerdrOwnedSessions.BootstrapForRecord(recordPath);
        if (!File.Exists(bootstrap)) { return null; }
        var piDirectory = _kind == InteractiveAgentKind.Pi && sessionId is not null
            ? _transcripts.FindPiSessionDirectory(Path.Combine(_stateRoot, "pi-sessions"), sessionId) : null;
        if (_kind == InteractiveAgentKind.Pi && piDirectory is null) { return null; }
        var launch = new InteractiveLaunch(_kind, Path.GetFileNameWithoutExtension(bootstrap),
            JobWorktree.WorkingDirectory(owner) ?? Environment.CurrentDirectory, sessionId, piDirectory, bootstrap)
        { JobId = owner.JobId, TabLabel = session.TabLabel, LiveReuse = true }.WithSelection(owner.Options);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (!control.RebindAsync(launch, session, timeout.Token).GetAwaiter().GetResult()) { return null; }
        }
        catch (Exception error) when (error is HerdrLaunchException or IOException or OperationCanceledException) { return null; }
        _rebound[recordPath] = launch;
        return launch;
    }

    // The pane must run exactly this native session now: an operator may have resumed or started
    // another one in the retained pane while the daemon was down. Pi's identity cannot be proven.
    bool BindTranscript(InteractiveLaunch launch, OwnedHerdrSession pane, string sessionId)
    {
        if (_kind == InteractiveAgentKind.Pi || pane.ShellPid is not { } shell) { return false; }
        var live = LiveSessionProbe is { } probe ? probe(launch, shell) : _transcripts.LiveSessions(launch, shell);
        if (live is not { Count: 1 } || !live.Contains(sessionId)) { return false; }
        launch.NativeTranscript ??= _transcripts.Locate(launch, sessionId);
        return launch.NativeTranscript?.SessionId == sessionId;
    }

    // One sample can read idle between steps of a turn that is still working.
    bool SettledIdle(InteractiveLaunch launch)
    {
        for (var sample = 0; ; sample++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (_control.StatusAsync(launch, timeout.Token).GetAwaiter().GetResult() is not (InteractiveAgentStatus.Idle or InteractiveAgentStatus.Done)) { return false; }
            }
            catch (Exception error) when (error is HerdrLaunchException or IOException or OperationCanceledException) { return false; }
            if (sample == 2) { return true; }
            Thread.Sleep(250);
        }
    }

    internal bool TakeIdleForNativeTurn(string sessionId, bool running = false) => _liveSessions.TakeForNativeTurn(sessionId, running);

    internal void RememberNativeTurn(string sessionId) => _liveSessions.RememberNativeTurn(sessionId);
    public void ReleaseNativeTurn(string sessionId) => _liveSessions.ReleaseNativeTurn(sessionId);
    void StopLaunch(InteractiveLaunch launch) { _control.StopOwned(launch); _liveSessions.Closed(launch); }

    void RememberSession(string sessionId, InteractiveLaunch launch) => _liveSessions.Remember(sessionId, launch);

    internal static string TabLabel(InteractiveAgentKind kind, string? name, string jobId) =>
        $"{kind.ToString().ToLowerInvariant()}: {(string.IsNullOrWhiteSpace(name) ? jobId[..Math.Min(jobId.Length, 8)] : name)}";

    void BindNativeSession(string sessionId, InteractiveLaunch launch)
    {
        _nativeSessions[sessionId] = launch;
        _liveSessions.Track(sessionId, launch);
    }

    InteractiveAgentStatus? CodexSessionStatus(string sessionId)
    {
        if (_kind != InteractiveAgentKind.Codex || !_nativeSessions.TryGetValue(sessionId, out var launch)
            || launch.NativeTranscript is not { SessionId: var bound } || bound != sessionId) { return null; }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return _control.StatusAsync(launch, timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HerdrLaunchException or IOException or OperationCanceledException) { return null; }
    }

    public bool HasLiveCodexSession(string sessionId) =>
        CodexSessionStatus(sessionId) is InteractiveAgentStatus.Idle or InteractiveAgentStatus.Working or InteractiveAgentStatus.Done;

    public bool HasWorkingCodexSession(string sessionId) => CodexSessionStatus(sessionId) == InteractiveAgentStatus.Working;

    public bool HasIdleCodexSession(string sessionId) =>
        CodexSessionStatus(sessionId) is InteractiveAgentStatus.Idle or InteractiveAgentStatus.Done;

    public bool HasIdleClaudeSession(string sessionId)
    {
        if (_kind != InteractiveAgentKind.Claude || !_nativeSessions.TryGetValue(sessionId, out var launch)
            || launch.NativeTranscript is not { SessionId: var bound } || bound != sessionId) { return false; }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return _control.StatusAsync(launch, timeout.Token).GetAwaiter().GetResult()
                is InteractiveAgentStatus.Idle or InteractiveAgentStatus.Done;
        }
        catch (Exception ex) when (ex is HerdrLaunchException or IOException or OperationCanceledException) { return false; }
    }

    public bool HasLiveClaudeSession(string sessionId)
    {
        if (_kind != InteractiveAgentKind.Claude || !_nativeSessions.TryGetValue(sessionId, out var launch)
            || launch.NativeTranscript is not { SessionId: var bound } || bound != sessionId) { return false; }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return _control.StatusAsync(launch, timeout.Token).GetAwaiter().GetResult()
                is InteractiveAgentStatus.Idle or InteractiveAgentStatus.Working or InteractiveAgentStatus.Done;
        }
        catch (Exception ex) when (ex is HerdrLaunchException or IOException or OperationCanceledException) { return false; }
    }

    public bool? HasLiveSession(string sessionId)
    {
        if (_control is HerdrAgentControl control && _nativeSessions.TryGetValue(sessionId, out var launch) && !control.IsBound(launch))
        {
            _liveSessions.Closed(launch);
        }
        return _liveSessions.Liveness(sessionId);
    }

    public bool HasIdleSession(string sessionId) => _liveSessions.IsAlive(sessionId, launch =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return _control.StatusAsync(launch, timeout.Token).GetAwaiter().GetResult() != InteractiveAgentStatus.Gone;
    });

    // Idle must hold across several samples: one can fall between steps of a turn that is still working.
    // A vanished pane is safe to close; an unreadable one is kept.
    async Task<bool> PaneIsIdleAsync(InteractiveLaunch launch) => await PaneIdleStateAsync(launch) == SessionIdleState.Idle;

    async Task<SessionIdleState> PaneIdleStateAsync(InteractiveLaunch launch)
    {
        try
        {
            for (var sample = 0; sample < 5; sample++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                switch (await _control.StatusAsync(launch, timeout.Token))
                {
                    case InteractiveAgentStatus.Gone: return SessionIdleState.Idle;
                    case InteractiveAgentStatus.Idle or InteractiveAgentStatus.Done: break;
                    case InteractiveAgentStatus.Working or InteractiveAgentStatus.Blocked: return SessionIdleState.Busy;
                    default: return SessionIdleState.Unverified;
                }
                if (sample < 4) { await Task.Delay(250); }
            }
            return SessionIdleState.Idle;
        }
        catch (Exception error) when (error is HerdrLaunchException or IOException or OperationCanceledException) { return SessionIdleState.Unverified; }
    }

    public object? LaunchIdentity(string sessionId) => _liveSessions.Identity(sessionId);

    public Task<SessionIdleState> ProbeIdleAsync(string sessionId) => _liveSessions.ProbeAsync(sessionId, PaneIdleStateAsync);

    public bool HasIdleJob(JobRecord job)
    {
        if (job.SessionId is not { } sessionId || !_nativeSessions.TryGetValue(sessionId, out var launch)
            || launch.JobId != job.JobId || launch.NativeTranscript?.SessionId != sessionId) { return false; }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return _control.StatusAsync(launch, timeout.Token).GetAwaiter().GetResult() is InteractiveAgentStatus.Idle or InteractiveAgentStatus.Done;
        }
        catch (Exception error) when (error is HerdrLaunchException or IOException or OperationCanceledException) { return false; }
    }

    internal bool HasCompletedTurn(JobRecord job, string correlation)
    {
        if (job.SessionId is not { } sessionId || !_nativeSessions.TryGetValue(sessionId, out var launch)
            || launch.JobId != job.JobId || launch.NativeTranscript?.SessionId != sessionId) { return false; }
        try
        {
            return _transcripts.Read(launch, "atf-corr:" + correlation, DateTimeOffset.MinValue)
                is { Completed: true, ApiError: null };
        }
        catch (IOException) { return false; }
    }

    // Only a verified Gone tab is dropped; a slow or failed probe keeps the prior
    // reuse path so it cannot surface as a start timeout that fences the session.
    bool RetainedTabMayBeLive(string sessionId)
    {
        try { return HasIdleSession(sessionId); }
        catch (Exception e) when (e is HerdrLaunchException or InteractiveTerminalUnavailableException or IOException or OperationCanceledException) { return true; }
    }

    public bool StopIdleSession(string sessionId) => _liveSessions.Stop(sessionId);

    public bool HasOwnedJobs(IReadOnlyList<string> jobIds) => HerdrOwnedSessions.Read(_stateRoot,
        message => throw new HerdrLaunchException(message)).Any(entry => entry.Session.JobId is { } id && jobIds.Contains(id));

    public bool StopOwnedJobs(IReadOnlyList<string> jobIds)
    {
        var stopped = _control is HerdrAgentControl control && control.StopJobs(_stateRoot, jobIds);
        if (stopped) { _liveSessions.ForgetJobs(jobIds); }
        return stopped;
    }

    public void ForgetStoppedJobs(IReadOnlyList<string> jobIds) => HerdrOwnedSessions.Forget(_stateRoot, jobIds);

    // Daemon shutdown is not an explicit request to close human-visible TUIs.
    public void StopAllIdleSessions() { }

    /// <summary>Closes an interrupted tab when its queued follow-up ends before claim.</summary>
    public void CloseUnclaimedSession(string sessionId) => StopIdleSession(sessionId);

    string PiDirectory(BackendRequest request)
    {
        var root = Path.Combine(_stateRoot, "pi-sessions");
        if (request.ResumeSessionId is { } id)
        {
            var found = _transcripts.FindPiSessionDirectory(root, id);
            return found ?? throw new BackendNotStartedException("Pi session to resume was not found");
        }
        // Job ids are untrusted strings; a random directory also keeps concurrent Pi runs isolated.
        return Path.Combine(root, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)));
    }

    sealed class Run(IHerdrAgentControl control, IInteractiveTranscriptReader transcripts, BackendRequest request,
        InteractiveLaunch launch, DateTimeOffset started, TimeSpan settleTimeout, TimeSpan startupTimeout, Action<string, InteractiveLaunch> rememberSession,
        Action<string, InteractiveLaunch> bindNativeSession, Action<InteractiveLaunch> stopLaunch, bool recovered = false) : IBackendRun
    {
        bool _agentExited;
        bool _promptReturned = recovered;
        readonly Lock _lifetime = new();
        // Cancelled before Escape is sent: a deferred prompt of an interrupted job must never be submitted.
        readonly CancellationTokenSource _delivery = new();
        bool _interrupted;
        bool _stopped;
        string? _sessionId = request.ResumeSessionId;
        int _loggedMessages;
        DateTimeOffset? _apiErrorSince;
        InteractiveApiError? _observedApiError;
        string? _reportedLimitDetails;
        int _apiErrorProgressCount;
        string? _lastControlError;
        public bool OwnedSessionStopped { get; private set; }
        public int? ProcessId => null; // The Herdr server owns the TUI process, not this daemon.
        public ProcessSnapshotRoot? SnapshotRoot => control.PaneShell(launch) is { } shell ? new(shell.Pid, launch.AgentName, shell.StartTicks) : null;

        public async Task DeliverAsync(CancellationToken cancellationToken)
        {
            var marker = "atf-corr:" + request.Correlation;
            var prompt = request.Instruction + "\n\n[AgentTeamForge correlation id: " + marker + " — internal marker, ignore this line]";
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _delivery.Token);
                await control.PromptAsync(launch, prompt, linked.Token);
                _promptReturned = true;
            }
            catch (AgentStartupBlockedException)
            {
                TerminateOwnedChild();
                throw;
            }
            catch (HerdrLaunchException e)
            {
                Console.Error.WriteLine($"[atf-daemon] prompt not sent for job {request.JobId}: {e.Message}");
                // Delivery is uncertain: report it as evidence (needs reconciliation) instead of
                // letting a Herdr control fault halt the whole dispatcher.
                return;
            }
            // Submitted exactly once. A missing transcript never proves non-delivery, so the
            // prompt is never resent; an unproven turn ends as needs_reconciliation instead.
        }

        public async IAsyncEnumerable<BackendEvidence> ReadEvidenceAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var acknowledged = false;
            var observedActivity = DateTimeOffset.MinValue;
            string? observedWaiting = null;
            var session = request.ResumeSessionId;
            if (session is not null)
            {
                bindNativeSession(session, launch);
                yield return new BackendEvidence.Session(request.Correlation, session);
            }
            // Only the native completion record of this correlated turn completes the job;
            // Herdr's idle/done classification alone can follow interim commentary.
            var quietSince = DateTimeOffset.UtcNow;
            var confirmationDeadline = DateTimeOffset.UtcNow.Add(startupTimeout);
            var seenMessages = 0;
            var controlFailures = 0;
            var goneSamples = 0;
            var blocked = false;
            var backgroundWait = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = await StatusAsync(cancellationToken);
                if (status is null && (acknowledged || _promptReturned))
                {
                    if (++controlFailures >= 3)
                    {
                        yield return new BackendEvidence.ProtocolError("interactive_control_failed", _lastControlError);
                        yield break;
                    }
                    await Task.Delay(250, cancellationToken);
                    continue;
                }
                controlFailures = 0;
                // Gone is derived from Herdr CLI and /proc probes (pane get, server scan), which a
                // loaded host can fail once; require consecutive samples before ending a live turn.
                if (status == InteractiveAgentStatus.Gone && ++goneSamples < 3)
                {
                    await Task.Delay(250, cancellationToken);
                    continue;
                }
                if (status != InteractiveAgentStatus.Gone) { goneSamples = 0; }
                var output = transcripts.Read(launch, "atf-corr:" + request.Correlation, started);
                if (output?.BindingError is { } bindingError)
                {
                    yield return new BackendEvidence.ProtocolError(bindingError);
                    yield break;
                }
                if (output is not null && !acknowledged)
                {
                    // A native user record proves that the one submitted prompt landed.
                    acknowledged = true;
                    yield return new BackendEvidence.Ack(request.Correlation);
                }
                if (output is not null && request.Output is { } log)
                {
                    for (var i = _loggedMessages; i < output.Progress.Count; i++)
                    {
                        log("transcript", Encoding.UTF8.GetBytes(output.ProgressLine(i) + "\n"));
                    }
                    _loggedMessages = output.Progress.Count;
                }
                if (output?.SessionId is { } nativeId && nativeId != session)
                {
                    session = nativeId;
                    _sessionId = nativeId;
                    bindNativeSession(nativeId, launch);
                    yield return new BackendEvidence.Session(request.Correlation, nativeId);
                }
                if (session is not null && output is { Completed: false } && (output.Interrupted || output.Superseded))
                {
                    yield return new BackendEvidence.AgentError("interactive_turn_interrupted", "Native turn was interrupted by a new turn or Escape.", TurnEnded: true);
                    yield break;
                }
                if (output?.ApiError is { } apiError && session is not null)
                {
                    if (apiError.Code == "agent_rate_limited")
                    {
                        if (_reportedLimitDetails != apiError.Message)
                        {
                            _reportedLimitDetails = apiError.Message;
                            yield return new BackendEvidence.AccountLimit(apiError.Message);
                        }
                    }
                    else
                    {
                        if (_observedApiError != apiError || _apiErrorProgressCount != output.Progress.Count)
                        {
                            _observedApiError = apiError;
                            _apiErrorProgressCount = output.Progress.Count;
                            _apiErrorSince = DateTimeOffset.UtcNow;
                        }
                        if (output.Completed || apiError.TurnEnded || DateTimeOffset.UtcNow - _apiErrorSince >= apiError.QuietWindow)
                        {
                            yield return new BackendEvidence.AgentError(apiError.Code, apiError.Message, output.Completed || apiError.TurnEnded);
                            yield break;
                        }
                    }
                }
                else { _apiErrorSince = null; _observedApiError = null; _reportedLimitDetails = null; }
                if (output is { Completed: true, ApiError: null } && session is not null)
                {
                    yield return new BackendEvidence.Result(request.Correlation, output.Message ?? "");
                    yield return new BackendEvidence.EndOfOutput();
                    yield break;
                }
                if (acknowledged && output?.ApiError is null)
                {
                    var activity = output?.LastActivityAt ?? started;
                    var waiting = DateTimeOffset.UtcNow - activity >= TimeSpan.FromSeconds(45) ? "awaiting_turn_end" : null;
                    if ((output?.LastActivityAt is not null || waiting is not null)
                        && (activity != observedActivity || waiting != observedWaiting))
                    {
                        observedActivity = activity;
                        observedWaiting = waiting;
                        yield return new BackendEvidence.TurnObservation(activity, waiting);
                    }
                }
                if (!acknowledged && _promptReturned && status == InteractiveAgentStatus.Gone)
                {
                    _agentExited = true;
                    yield return new BackendEvidence.ProtocolError("interactive_agent_exited");
                    yield break;
                }
                if (!acknowledged)
                {
                    if (_promptReturned && launch.Kind == InteractiveAgentKind.Pi && await DeliveryBlockerAsync(cancellationToken) is { } login)
                    {
                        // Pi refused the prompt before recording it: nothing ran, so the pane goes too.
                        TerminateOwnedChild();
                        yield return new BackendEvidence.AgentError(login.Reason, login.Message);
                        yield break;
                    }
                    if (status == InteractiveAgentStatus.Unverified)
                    {
                        confirmationDeadline = DateTimeOffset.UtcNow.Add(startupTimeout);
                    }
                    else if (DateTimeOffset.UtcNow >= confirmationDeadline)
                    {
                        yield return new BackendEvidence.ProtocolError("interactive_delivery_not_confirmed");
                        yield break;
                    }
                    await Task.Delay(250, cancellationToken);
                    continue;
                }
                var nowBlocked = status == InteractiveAgentStatus.Blocked && output?.ApiError?.Code != "agent_rate_limited";
                if (nowBlocked != blocked)
                {
                    blocked = nowBlocked;
                    yield return new BackendEvidence.AgentBlocked(blocked);
                }
                // The agent ended its turn on a background task (e.g. a long build) and waits for it to
                // report back; the turn stays running. Reattach after a restart clears the marker.
                if (output is not null && output.WaitingOnBackground != backgroundWait)
                {
                    backgroundWait = output.WaitingOnBackground;
                    yield return new BackendEvidence.BackgroundWait(output.WaitingOnBackground);
                }
                if (status == InteractiveAgentStatus.Gone)
                {
                    // Nothing left to observe; close our owned session on dispose.
                    _agentExited = true;
                    yield return new BackendEvidence.ProtocolError("interactive_agent_exited");
                    yield break;
                }
                var progressed = output is not null && output.Progress.Count != seenMessages;
                seenMessages = output?.Progress.Count ?? 0;
                if (status is not (InteractiveAgentStatus.Idle or InteractiveAgentStatus.Done) || progressed
                    || output is { PendingBackgroundTasks: true } || output?.ApiError?.Code == "agent_rate_limited")
                {
                    quietSince = DateTimeOffset.UtcNow;
                }
                else if (DateTimeOffset.UtcNow - quietSince >= settleTimeout)
                {
                    // Idle without this turn's completion record (delayed, missing or unreadable
                    // transcript, or interim text): keep the tab, never claim success or resend.
                    yield return new BackendEvidence.ProtocolError("interactive_completion_unobserved");
                    yield break;
                }
                await Task.Delay(250, cancellationToken);
            }
        }

        async Task<AgentStartupBlockedException?> DeliveryBlockerAsync(CancellationToken cancellationToken)
        {
            try { return await control.DeliveryBlockerAsync(launch, cancellationToken); }
            catch (HerdrLaunchException) { return null; } // A failed screen read is no evidence either way.
        }

        async Task<InteractiveAgentStatus?> StatusAsync(CancellationToken cancellationToken)
        {
            try { return await control.StatusAsync(launch, cancellationToken); }
            catch (HerdrLaunchException error) { _lastControlError = error.Message; return null; }
        }

        public void TerminateOwnedChild()
        {
            lock (_lifetime)
            {
                stopLaunch(launch);
                OwnedSessionStopped = true;
                _stopped = true;
            }
        }

        public void InterruptTurn()
        {
            // Preserve the same verified pane for the follow-up. Escape interrupts
            // the TUI turn without terminating its agent process.
            // Serialized with dispose: a tab closed by a settled turn is never
            // handed to the follow-up, which then resumes in a fresh tab.
            lock (_lifetime)
            {
                if (_stopped)
                {
                    return;
                }
                _interrupted = true;
                _delivery.Cancel();
                control.InterruptAsync(launch, CancellationToken.None).GetAwaiter().GetResult();
                if (_sessionId is { } sessionId)
                {
                    rememberSession(sessionId, launch);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            // A settled turn keeps its proven tab available for follow-up or Stop agent.
            lock (_lifetime)
            {
                if (_stopped || _interrupted)
                {
                    return ValueTask.CompletedTask;
                }
                _stopped = true;
                // A Gone status is a probe result; close the pane only with process proof that it is gone.
                if (_agentExited && control.PaneIsGone(launch))
                {
                    stopLaunch(launch);
                    OwnedSessionStopped = true;
                }
                else if (_sessionId is { } sessionId)
                {
                    rememberSession(sessionId, launch);
                }
                // Unknown native identity still has durable ownership for explicit stop.
            }
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed record InteractiveLaunch(InteractiveAgentKind Kind, string AgentName, string WorkingDirectory,
    string? ResumeSessionId, string? PiSessionDirectory, string BootstrapPath)
{
    // Kept with the owned pane by RetainedSessions, including fresh-launch follow-ups.
    public NativeTranscriptBinding? NativeTranscript { get; set; }
    public Action<string>? StartupProgress { get; init; }
    /// <summary>A retained pane that already passed startup; its screen is never a setup screen.</summary>
    public bool LiveReuse { get; init; }
    public string? JobId { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? HerdrPlacement { get; init; }
    public string? TabLabel { get; init; }

    public InteractiveLaunch WithSelection(string options)
    {
        var selection = Selection(options);
        return this with { Model = selection.Model, Effort = selection.Effort };
    }

    public static (string? Model, string? Effort) Selection(string options)
    {
        var pairs = options.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2).ToArray();
        return (pairs.FirstOrDefault(pair => pair[0] == "model")?.ElementAtOrDefault(1),
            pairs.FirstOrDefault(pair => pair[0] == "effort")?.ElementAtOrDefault(1));
    }
}

internal enum InteractiveAgentStatus { Idle, Working, Done, Blocked, Unknown, Unverified, Gone }

internal interface IHerdrAgentControl
{
    Task StartAsync(InteractiveLaunch launch, CancellationToken cancellationToken);
    Task PromptAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken);
    Task InterruptAsync(InteractiveLaunch launch, CancellationToken cancellationToken);
    Task<InteractiveAgentStatus> StatusAsync(InteractiveLaunch launch, CancellationToken cancellationToken);
    void StopOwned(InteractiveLaunch launch);

    /// <summary>The agent's own sign-in error for a sent prompt it never recorded; null when none is on screen.</summary>
    Task<AgentStartupBlockedException?> DeliveryBlockerAsync(InteractiveLaunch launch, CancellationToken cancellationToken) =>
        Task.FromResult<AgentStartupBlockedException?>(null);

    /// <summary>True only with process proof that the launch's server or pane shell no longer exists.</summary>
    bool PaneIsGone(InteractiveLaunch launch) => true;

    /// <summary>The bound pane shell's PID and start identity; null while unbound.</summary>
    ProcessIdentity? PaneShell(InteractiveLaunch launch) => null;
}

internal interface IInteractiveTranscriptReader
{
    InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started);
    string? FindPiSessionDirectory(string root, string sessionId);

    /// <summary>The main transcript whose header names exactly this native session, in the launch's config root.</summary>
    NativeTranscriptBinding? Locate(InteractiveLaunch launch, string sessionId) => null;

    /// <summary>The newest turn carrying one of these ATF correlations, from one read of the bound transcript.</summary>
    InteractiveTranscript? ReadLatestTurn(InteractiveLaunch launch, IReadOnlyCollection<string> correlations) => null;

    /// <summary>The native sessions running under a pane's shell now; null when that cannot be established.</summary>
    IReadOnlySet<string>? LiveSessions(InteractiveLaunch launch, int shellPid) => null;
}

internal sealed record InteractiveTranscript(string SessionId, string? Message, IReadOnlyList<string>? Messages = null, bool Completed = false,
    bool PendingBackgroundTasks = false, string? BindingError = null, InteractiveApiError? ApiError = null, IReadOnlyList<string?>? Times = null,
    bool Superseded = false, bool Incomplete = false)
{
    /// <summary>The latest assistant record ended the turn while background work is still pending (Claude only).</summary>
    public bool WaitingOnBackground { get; init; }
    public DateTimeOffset? LastActivityAt { get; init; }
    public bool Interrupted { get; init; }

    public IReadOnlyList<string> Progress => Messages ?? [];

    /// <summary>The log line for message i: JSON carrying the native timestamp when the record has one, else plain text.</summary>
    public string ProgressLine(int i)
    {
        if (Times is not { } times || i >= times.Count || times[i] is not { Length: > 0 } ts) { return Progress[i]; }
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", JobActivity.TranscriptType);
            writer.WriteString("timestamp", ts);
            writer.WriteString("text", Progress[i]);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>
/// A Claude API-error record ending the transcript. Claude can still continue the turn on its own
/// (e.g. seconds after "Connection lost mid-response"), so ordinary API errors settle only once
/// the turn has ended or the transcript stays quiet. Rate limits keep the live TUI under observation.
/// </summary>
internal sealed record InteractiveApiError(string Code, string Message, bool TurnEnded = false)
{
    public TimeSpan QuietWindow => TurnEnded || Code == "agent_login_required" ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30);
}

internal sealed record NativeTranscriptBinding(string SessionId, string Path);
