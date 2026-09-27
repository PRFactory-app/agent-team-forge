using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

public enum InteractiveAgentKind { Claude, Codex, Pi }

/// <summary>Runs a real agent TUI in a tab of an ATF-owned Herdr session.</summary>
public sealed class HerdrInteractiveBackend : IJobBackend, IInteractiveSessionStop
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

    public HerdrInteractiveBackend(HerdrTerminal terminal, InteractiveAgentKind kind, string stateRoot)
        : this(new HerdrAgentControl(terminal), new InteractiveTranscriptReader(terminal.Env), kind, stateRoot) { }

    // settleTimeout: how long an idle agent may go without native completion before the turn is uncertain.
    internal HerdrInteractiveBackend(IHerdrAgentControl control, IInteractiveTranscriptReader transcripts, InteractiveAgentKind kind, string stateRoot,
        TimeSpan? settleTimeout = null, TimeSpan? startupTimeout = null)
    {
        _control = control;
        _transcripts = transcripts;
        _kind = kind;
        _stateRoot = stateRoot;
        _liveSessions = new RetainedSessions(control.StopOwned);
        _settleTimeout = settleTimeout ?? TimeSpan.FromSeconds(60);
        _startupTimeout = startupTimeout ?? InteractiveStartup.Timeout;
    }

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
                return new Run(_control, _transcripts, request, live with { StartupProgress = request.StartupProgress, LiveReuse = true }, started, _settleTimeout, _startupTimeout, RememberSession, BindNativeSession);
            }
            _control.StopOwned(live);
        }

        var agentName = "atf" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10));
        var piDirectory = _kind == InteractiveAgentKind.Pi ? PiDirectory(request) : null;
        var launch = new InteractiveLaunch(_kind, agentName, cwd, request.ResumeSessionId, piDirectory,
            Path.Combine(_stateRoot, "herdr", agentName + ".bootstrap"))
        { StartupProgress = request.StartupProgress, JobId = request.JobId, HerdrPlacement = AgentTeamForge.Business.Features.Jobs.JobOptions.Read(request.Options, "herdr_placement") }.WithSelection(request.Options);
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
        return new Run(_control, _transcripts, request, launch, started, _settleTimeout, _startupTimeout, RememberSession, BindNativeSession);
    }

    void RememberSession(string sessionId, InteractiveLaunch launch) => _liveSessions.Remember(sessionId, launch);

    void BindNativeSession(string sessionId, InteractiveLaunch launch) => _nativeSessions[sessionId] = launch;

    public bool HasLiveCodexSession(string sessionId)
    {
        if (_kind != InteractiveAgentKind.Codex || !_nativeSessions.TryGetValue(sessionId, out var launch)
            || launch.NativeTranscript is not { SessionId: var bound } || bound != sessionId) { return false; }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return _control.StatusAsync(launch, timeout.Token).GetAwaiter().GetResult()
                is InteractiveAgentStatus.Idle or InteractiveAgentStatus.Working or InteractiveAgentStatus.Done;
        }
        catch (Exception ex) when (ex is HerdrLaunchException or IOException or OperationCanceledException) { return false; }
    }

    public bool HasIdleSession(string sessionId) => _liveSessions.IsAlive(sessionId, launch =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return _control.StatusAsync(launch, timeout.Token).GetAwaiter().GetResult() != InteractiveAgentStatus.Gone;
    });

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
        Action<string, InteractiveLaunch> bindNativeSession) : IBackendRun
    {
        bool _agentExited;
        bool _promptReturned;
        readonly Lock _lifetime = new();
        bool _interrupted;
        bool _stopped;
        string? _sessionId = request.ResumeSessionId;
        int _loggedMessages;
        DateTimeOffset? _apiErrorSince;
        InteractiveApiError? _observedApiError;
        string? _reportedLimitDetails;
        int _apiErrorProgressCount;
        public bool OwnedSessionStopped { get; private set; }
        public int? ProcessId => null; // The Herdr server owns the TUI process, not this daemon.

        public async Task DeliverAsync(CancellationToken cancellationToken)
        {
            var marker = "atf-corr:" + request.Correlation;
            var prompt = request.Instruction + "\n\n[AgentTeamForge correlation id: " + marker + " — internal marker, ignore this line]";
            try
            {
                await control.PromptAsync(launch, prompt, cancellationToken);
                _promptReturned = true;
            }
            catch (AgentStartupBlockedException)
            {
                TerminateOwnedChild();
                throw;
            }
            catch (HerdrLaunchException)
            {
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
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = await StatusAsync(cancellationToken);
                if (status is null && (acknowledged || _promptReturned))
                {
                    yield return new BackendEvidence.ProtocolError("interactive_control_failed");
                    yield break;
                }
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
                        log("transcript", Encoding.UTF8.GetBytes(output.Progress[i] + "\n"));
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
                        if (DateTimeOffset.UtcNow - _apiErrorSince >= apiError.QuietWindow)
                        {
                            yield return new BackendEvidence.AgentError(apiError.Code, apiError.Message);
                            yield break;
                        }
                    }
                }
                else { _apiErrorSince = null; _observedApiError = null; _reportedLimitDetails = null; }
                if (output is { Completed: true, ApiError: null, Message: { Length: > 0 } message } && session is not null)
                {
                    yield return new BackendEvidence.Result(request.Correlation, message);
                    yield return new BackendEvidence.EndOfOutput();
                    yield break;
                }
                if (!acknowledged && _promptReturned && status == InteractiveAgentStatus.Gone)
                {
                    _agentExited = true;
                    yield return new BackendEvidence.ProtocolError("interactive_agent_exited");
                    yield break;
                }
                if (!acknowledged)
                {
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
                if (status == InteractiveAgentStatus.Blocked && output?.ApiError?.Code != "agent_rate_limited")
                {
                    yield return new BackendEvidence.ProtocolError("interactive_agent_blocked");
                    yield break;
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

        async Task<InteractiveAgentStatus?> StatusAsync(CancellationToken cancellationToken)
        {
            try { return await control.StatusAsync(launch, cancellationToken); }
            catch (HerdrLaunchException) { return null; }
        }

        public void TerminateOwnedChild()
        {
            lock (_lifetime)
            {
                control.StopOwned(launch);
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
                if (!_agentExited && _sessionId is { } sessionId)
                {
                    rememberSession(sessionId, launch);
                }
                else if (_agentExited)
                {
                    control.StopOwned(launch);
                    OwnedSessionStopped = true;
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
}

internal interface IInteractiveTranscriptReader
{
    InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started);
    string? FindPiSessionDirectory(string root, string sessionId);
}

internal sealed record InteractiveTranscript(string SessionId, string? Message, IReadOnlyList<string>? Messages = null, bool Completed = false,
    bool PendingBackgroundTasks = false, string? BindingError = null, InteractiveApiError? ApiError = null)
{
    public IReadOnlyList<string> Progress => Messages ?? [];
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
