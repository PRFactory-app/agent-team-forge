using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>Runs an interactive agent in an owned Windows Terminal tab.</summary>
public sealed class WtInteractiveBackend : IJobBackend, IInteractiveSessionStop, IDisposable
{
    readonly IWtTabControl _tabs;
    readonly IInteractiveTranscriptReader _transcripts;
    readonly InteractiveAgentKind _kind;
    readonly string _stateRoot;
    readonly string _tabDirectory;
    readonly RetainedSessions _liveSessions;
    readonly ConcurrentDictionary<string, InteractiveLaunch> _jobs = [];
    readonly TimeSpan _startupTimeout;
    readonly Action<InteractiveAgentKind, string>? _configPreflight;

    public WtInteractiveBackend(InteractiveAgentKind kind, string stateRoot, TimeSpan? idleTimeout = null, Func<InteractiveRetentionSettings>? retentionSettings = null)
        : this(new WtTabControl(), new InteractiveTranscriptReader(), kind, stateRoot, "wt", configPreflight: InteractiveAgentPreflight.CheckCurrent, idleTimeout: idleTimeout, retentionSettings: retentionSettings)
    {
        // Idle agents outlive a daemon restart; without adoption follow-up, Stop agent and idle close never reach them.
        foreach (var (sessionId, launch) in WtTabControl.Survivors(stateRoot, kind))
        {
            AdoptRetained(sessionId, launch);
        }
    }

    internal WtInteractiveBackend(IWtTabControl tabs, IInteractiveTranscriptReader transcripts, InteractiveAgentKind kind, string stateRoot, TimeSpan? idleTimeout = null, Func<InteractiveRetentionSettings>? retentionSettings = null)
        : this(tabs, transcripts, kind, stateRoot, "wt", idleTimeout: idleTimeout, retentionSettings: retentionSettings) { }

    internal WtInteractiveBackend(IWtTabControl tabs, IInteractiveTranscriptReader transcripts, InteractiveAgentKind kind, string stateRoot, string tabDirectory, TimeSpan? startupTimeout = null,
        Action<InteractiveAgentKind, string>? configPreflight = null, TimeSpan? idleTimeout = null, TimeProvider? timeProvider = null, Func<InteractiveRetentionSettings>? retentionSettings = null)
    {
        _tabs = tabs;
        _transcripts = transcripts;
        _kind = kind;
        _stateRoot = stateRoot;
        _tabDirectory = tabDirectory;
        _liveSessions = new RetainedSessions(tabs.StopOwned, idleTimeout, timeProvider, retentionSettings, tabs.Retained, tabs.Busy);
        _startupTimeout = startupTimeout ?? InteractiveStartup.Timeout;
        _configPreflight = configPreflight;
    }

    public void Dispose() => _liveSessions.Dispose();

    public IBackendRun Start(BackendRequest request)
    {
        var cwd = request.WorkingDirectory ?? Environment.CurrentDirectory;
        if (!Path.IsPathFullyQualified(cwd) || !Directory.Exists(cwd))
        {
            throw new BackendNotStartedException("interactive working directory does not exist");
        }

        _tabs.Preflight(_kind);
        _configPreflight?.Invoke(_kind, cwd);
        if (request.ResumeSessionId is { } resumeId && _liveSessions.TryTake(resumeId, out var previous))
        {
            try { StopLaunch(previous); }
            catch
            {
                _liveSessions.Remember(resumeId, previous);
                throw;
            }
        }
        var agentName = "atf" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10));
        var piDirectory = _kind == InteractiveAgentKind.Pi ? PiDirectory(request) : null;
        var launch = new InteractiveLaunch(_kind, agentName, cwd, request.ResumeSessionId, piDirectory,
            Path.Combine(_stateRoot, _tabDirectory, agentName + (_tabDirectory == "wt" ? ".launch.ps1" : ".launch.sh")))
        { JobId = request.JobId, TabLabel = HerdrInteractiveBackend.TabLabel(_kind, request.DisplayName, request.JobId) }.WithSelection(request.Options);
        if (OperatingSystem.IsWindows())
        {
            _ = WtTabControl.AgentArguments(launch, "");
        }
        _jobs[request.JobId] = launch;
        return new Run(_tabs, _transcripts, request, launch, DateTimeOffset.UtcNow, _startupTimeout, _liveSessions.Remember, BindNativeSession, StopLaunch);
    }

    /// <summary>
    /// Stops the job's owned tab. True once nothing owned can run: the tab was stopped, or its agent is proven
    /// gone. An identity that cannot be verified either way keeps the tab owned and returns false.
    /// </summary>
    public bool StopOwnedJob(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var launch) && !TryRecoverLaunch(jobId, out launch)) { return false; }
        if (_tabs.IsAlive(launch))
        {
            StopLaunch(launch);
            if (_tabs.IsAlive(launch)) { return false; }
        }
        else
        {
            var gone = _tabs.ProvenGone(launch);
            // Also removes the records of an exited agent, or makes a wrapper that never reported fail to start.
            StopLaunch(launch);
            if (!gone && !_tabs.ProvenGone(launch)) { return false; }
        }
        _jobs.TryRemove(jobId, out _);
        return true;
    }

    /// <summary>
    /// After a restart, a native turn of <paramref name="sessionId"/> may still run in the tab one of
    /// <paramref name="ownerJobIds"/> launched. While that tab lives it is held busy, never swept or reused
    /// as idle, until the turn settles or is released; false when no such tab is live.
    /// </summary>
    internal bool HoldNativeTurn(string sessionId, IEnumerable<string> ownerJobIds)
    {
        foreach (var jobId in ownerJobIds)
        {
            if ((_jobs.TryGetValue(jobId, out var launch) || TryRecoverLaunch(jobId, out launch)) && _tabs.IsAlive(launch))
            {
                _liveSessions.Hold(sessionId, launch);
                return true;
            }
        }
        return false;
    }

    /// <summary>The tab this daemon, or the one before a restart, launched for the job still runs its agent.</summary>
    public bool OwnsLiveJob(string jobId) =>
        (_jobs.TryGetValue(jobId, out var launch) || TryRecoverLaunch(jobId, out launch)) && _tabs.IsAlive(launch);

    bool TryRecoverLaunch(string jobId, out InteractiveLaunch launch)
    {
        launch = null!;
        if (_tabs is not (WtTabControl or MacTabControl) ||
            FindRecoveredLaunch(_stateRoot, _kind, jobId, _tabDirectory) is not { } found) { return false; }
        launch = found;
        _jobs[jobId] = launch;
        return true;
    }

    internal static InteractiveLaunch? FindRecoveredLaunch(string stateRoot, InteractiveAgentKind kind, string jobId, string tabDirectory = "wt")
    {
        var directory = Path.Combine(stateRoot, tabDirectory);
        if (!Directory.Exists(directory)) { return null; }
        foreach (var path in Directory.EnumerateFiles(directory, "atf*.launch.job"))
        {
            var file = new FileInfo(path);
            if (file.LinkTarget is not null || file.Length > 64) { continue; }
            try
            {
                if (File.ReadAllText(path, Encoding.ASCII) != jobId) { continue; }
            }
            catch (IOException) { continue; }
            var name = Path.GetFileName(path);
            if (!name.EndsWith(".launch.job", StringComparison.Ordinal)) { continue; }
            var agentName = name[..^".launch.job".Length];
            return new InteractiveLaunch(kind, agentName, stateRoot, null, null,
                Path.ChangeExtension(path, tabDirectory == "wt" ? ".ps1" : ".sh"))
            { JobId = jobId };
        }
        return null;
    }

    internal bool TakeIdleForNativeTurn(string sessionId, bool running = false) => _liveSessions.TakeForNativeTurn(sessionId, running);

    internal void RememberNativeTurn(string sessionId) => _liveSessions.RememberNativeTurn(sessionId);
    public void ReleaseNativeTurn(string sessionId) => _liveSessions.ReleaseNativeTurn(sessionId);
    void BindNativeSession(string sessionId, InteractiveLaunch launch) => _liveSessions.Track(sessionId, launch);
    void StopLaunch(InteractiveLaunch launch) { _tabs.StopOwned(launch); _liveSessions.Closed(launch); }

    /// <summary>Retain an idle agent that outlived a daemon restart, so follow-up, Stop agent and idle close reach it.</summary>
    internal void AdoptRetained(string sessionId, InteractiveLaunch launch) => _liveSessions.Remember(sessionId, launch);

    public bool? HasLiveSession(string sessionId) => _liveSessions.Liveness(sessionId);

    public bool HasIdleSession(string sessionId) => _liveSessions.IsAlive(sessionId, _tabs.IsAlive);

    public bool HasLiveClaudeSession(string sessionId) => _kind == InteractiveAgentKind.Claude
        && _jobs.Values.Any(launch => launch.NativeTranscript?.SessionId == sessionId && _tabs.IsAlive(launch));

    public bool HasIdleClaudeSession(string sessionId) => _kind == InteractiveAgentKind.Claude
        && _liveSessions.IsAlive(sessionId, launch => launch.NativeTranscript?.SessionId == sessionId && _tabs.IsAlive(launch));

    public bool StopIdleSession(string sessionId) => _liveSessions.Stop(sessionId);

    public void StopAllIdleSessions() => _liveSessions.StopAll();

    string PiDirectory(BackendRequest request)
    {
        var root = Path.Combine(_stateRoot, "pi-sessions");
        if (request.ResumeSessionId is { } id)
        {
            return _transcripts.FindPiSessionDirectory(root, id)
                ?? throw new BackendNotStartedException("Pi session to resume was not found");
        }
        return Path.Combine(root, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)));
    }

    sealed class Run(IWtTabControl tabs, IInteractiveTranscriptReader transcripts, BackendRequest request,
        InteractiveLaunch launch, DateTimeOffset started, TimeSpan startupTimeout, Action<string, InteractiveLaunch> rememberSession,
        Action<string, InteractiveLaunch> bindNativeSession, Action<InteractiveLaunch> stopLaunch) : IBackendRun
    {
        bool _stopped;
        // Only a turn the evidence loop saw settle leaves its tab idle; any other live tab may still be working.
        volatile bool _settled;
        readonly Lock _lifetime = new();
        string? _sessionId = request.ResumeSessionId;
        string? _notStartedError;
        int _loggedMessages;
        bool _backgroundWait;
        DateTimeOffset? _apiErrorSince;
        InteractiveApiError? _observedApiError;
        string? _reportedLimitDetails;
        int _apiErrorProgressCount;
        public int? ProcessId => tabs.ProcessId(launch);
        // The tab's agent carries the launch's marker, which outlives this turn (see MacTabControl.WrapperText).
        public ProcessSnapshotRoot? SnapshotRoot => ProcessId is int pid ? new(pid, launch.AgentName) : null;

        public async Task DeliverAsync(CancellationToken cancellationToken)
        {
            var marker = "atf-corr:" + request.Correlation;
            var prompt = request.Instruction + "\n\n[AgentTeamForge correlation id: " + marker + " — internal marker, ignore this line]";
            try
            {
                await tabs.StartAsync(launch, prompt, cancellationToken);
            }
            catch (BackendNotStartedException ex)
            {
                _notStartedError = ex.Message;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                // wt.exe can hand the tab to an existing window before returning an error.
                // A failed launch is uncertain, so the dispatcher must reconcile it.
            }
        }

        public async IAsyncEnumerable<BackendEvidence> ReadEvidenceAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var acknowledged = false;
            var confirmationDeadline = DateTimeOffset.UtcNow.Add(startupTimeout);
            var session = request.ResumeSessionId;
            if (session is not null)
            {
                bindNativeSession(session, launch);
                yield return new BackendEvidence.Session(request.Correlation, session);
            }
            if (_notStartedError is { } notStarted)
            {
                yield return new BackendEvidence.NotStarted(notStarted);
                yield break;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var output = transcripts.Read(launch, "atf-corr:" + request.Correlation, started);
                if (output is not null && !acknowledged)
                {
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
                            _settled = true;
                            yield return new BackendEvidence.AgentError(apiError.Code, apiError.Message);
                            yield break;
                        }
                    }
                }
                else { _apiErrorSince = null; _observedApiError = null; _reportedLimitDetails = null; }
                if (output is { Completed: true, ApiError: null, Message: { Length: > 0 } message } && session is not null)
                {
                    _settled = true;
                    yield return new BackendEvidence.Result(request.Correlation, message);
                    yield return new BackendEvidence.EndOfOutput();
                    yield break;
                }
                // The agent ended its turn on a background task and waits for it to report back; the turn
                // stays running. Reattach after a restart clears the marker.
                if (acknowledged && output is not null && output.WaitingOnBackground != _backgroundWait)
                {
                    _backgroundWait = output.WaitingOnBackground;
                    yield return new BackendEvidence.BackgroundWait(output.WaitingOnBackground);
                }
                if (!acknowledged)
                {
                    // Only a recorded start error proves the agent never ran. A wrapper that
                    // exited without one may have run the agent (the prompt is in its argv),
                    // so that turn is uncertain and must be reconciled.
                    var exited = tabs.WrapperExited(launch);
                    if (tabs.StartFailure(launch) is { } failure)
                    {
                        stopLaunch(launch);
                        yield return new BackendEvidence.LaunchFailed(failure);
                        yield break;
                    }
                    if (tabs.LoginBlocker(launch) is { } login)
                    {
                        // The agent cannot have run the prompt; close its tab like a failed start.
                        stopLaunch(launch);
                        yield return login;
                        yield break;
                    }
                    if (exited && transcripts.Read(launch, "atf-corr:" + request.Correlation, started) is null)
                    {
                        yield return new BackendEvidence.ProtocolError("interactive_agent_exited");
                        yield break;
                    }
                    if (tabs.IsUnverified(launch))
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
                if (!tabs.IsAlive(launch))
                {
                    yield return new BackendEvidence.ProtocolError("interactive_agent_exited");
                    yield break;
                }
                await Task.Delay(250, cancellationToken);
            }
        }

        public void TerminateOwnedChild()
        {
            lock (_lifetime)
            {
                _stopped = true;
                stopLaunch(launch);
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_lifetime)
            {
                if (_stopped)
                {
                    return ValueTask.CompletedTask;
                }
                _stopped = true;
                if (tabs.IsAlive(launch))
                {
                    // Only a settled turn's tab is idle and may be retained (and recorded for a restarted
                    // daemon), where idle close or a follow-up can reach it. An unsettled turn may still be
                    // working, so its tab stays with the fenced job for explicit stop; never abandon or idle-close it.
                    if (_settled && _sessionId is { } sessionId) { rememberSession(sessionId, launch); }
                }
                else { stopLaunch(launch); }
            }
            return ValueTask.CompletedTask;
        }
    }
}

internal interface IWtTabControl
{
    void Preflight(InteractiveAgentKind kind);
    Task StartAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken);
    bool IsAlive(InteractiveLaunch launch);
    bool IsUnverified(InteractiveLaunch launch) => false;
    string? StartFailure(InteractiveLaunch launch);
    bool WrapperExited(InteractiveLaunch launch);
    int? ProcessId(InteractiveLaunch launch);
    void StopOwned(InteractiveLaunch launch);
    /// <summary>The settled turn left this launch idle for <paramref name="sessionId"/>.</summary>
    void Retained(InteractiveLaunch launch, string sessionId) { }
    /// <summary>A turn runs in this launch again: withdraw its idle record.</summary>
    void Busy(InteractiveLaunch launch) { }
    /// <summary>Process proof that the launch's agent exited or can never start; false while that is unverified.</summary>
    bool ProvenGone(InteractiveLaunch launch) => false;
    /// <summary>The agent's own proof that it cannot run any prompt without signing in; null when there is none.</summary>
    BackendEvidence.AgentError? LoginBlocker(InteractiveLaunch launch) => null;
}
