using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>Runs an interactive agent in an owned Windows Terminal tab.</summary>
public sealed class WtInteractiveBackend : IJobBackend, IInteractiveSessionStop
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

    public WtInteractiveBackend(InteractiveAgentKind kind, string stateRoot)
        : this(new WtTabControl(), new InteractiveTranscriptReader(), kind, stateRoot, "wt", configPreflight: InteractiveAgentPreflight.CheckCurrent) { }

    internal WtInteractiveBackend(IWtTabControl tabs, IInteractiveTranscriptReader transcripts, InteractiveAgentKind kind, string stateRoot)
        : this(tabs, transcripts, kind, stateRoot, "wt") { }

    internal WtInteractiveBackend(IWtTabControl tabs, IInteractiveTranscriptReader transcripts, InteractiveAgentKind kind, string stateRoot, string tabDirectory, TimeSpan? startupTimeout = null,
        Action<InteractiveAgentKind, string>? configPreflight = null)
    {
        _tabs = tabs;
        _transcripts = transcripts;
        _kind = kind;
        _stateRoot = stateRoot;
        _tabDirectory = tabDirectory;
        _liveSessions = new RetainedSessions(tabs.StopOwned);
        _startupTimeout = startupTimeout ?? InteractiveStartup.Timeout;
        _configPreflight = configPreflight;
    }

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
            try { _tabs.StopOwned(previous); }
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
        return new Run(_tabs, _transcripts, request, launch, DateTimeOffset.UtcNow, _startupTimeout, RememberSession);
    }

    public bool StopOwnedJob(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var launch) && !TryRecoverLaunch(jobId, out launch)) { return false; }
        if (!_tabs.IsAlive(launch)) { return false; }
        _tabs.StopOwned(launch);
        if (_tabs.IsAlive(launch)) { return false; }
        _jobs.TryRemove(jobId, out _);
        return true;
    }

    bool TryRecoverLaunch(string jobId, out InteractiveLaunch launch)
    {
        launch = null!;
        if (_tabs is not WtTabControl || _tabDirectory != "wt" ||
            FindRecoveredLaunch(_stateRoot, _kind, jobId) is not { } found) { return false; }
        launch = found;
        _jobs[jobId] = launch;
        return true;
    }

    internal static InteractiveLaunch? FindRecoveredLaunch(string stateRoot, InteractiveAgentKind kind, string jobId)
    {
        var directory = Path.Combine(stateRoot, "wt");
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
                Path.ChangeExtension(path, ".ps1"))
            { JobId = jobId };
        }
        return null;
    }

    void RememberSession(string sessionId, InteractiveLaunch launch) => _liveSessions.Remember(sessionId, launch);

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
        InteractiveLaunch launch, DateTimeOffset started, TimeSpan startupTimeout, Action<string, InteractiveLaunch> rememberSession) : IBackendRun
    {
        bool _stopped;
        readonly Lock _lifetime = new();
        string? _sessionId = request.ResumeSessionId;
        string? _notStartedError;
        int _loggedMessages;
        DateTimeOffset? _apiErrorSince;
        InteractiveApiError? _observedApiError;
        string? _reportedLimitDetails;
        int _apiErrorProgressCount;
        public int? ProcessId => tabs.ProcessId(launch);

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
                        log("transcript", Encoding.UTF8.GetBytes(output.Progress[i] + "\n"));
                    }
                    _loggedMessages = output.Progress.Count;
                }
                if (output?.SessionId is { } nativeId && nativeId != session)
                {
                    session = nativeId;
                    _sessionId = nativeId;
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
                if (!acknowledged)
                {
                    // Only a recorded start error proves the agent never ran. A wrapper that
                    // exited without one may have run the agent (the prompt is in its argv),
                    // so that turn is uncertain and must be reconciled.
                    var exited = tabs.WrapperExited(launch);
                    if (tabs.StartFailure(launch) is { } failure)
                    {
                        tabs.StopOwned(launch);
                        yield return new BackendEvidence.LaunchFailed(failure);
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
                tabs.StopOwned(launch);
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
                    if (_sessionId is { } sessionId) { rememberSession(sessionId, launch); }
                    // An uncertain turn may have no native session ID yet. Keep its
                    // verified tab for explicit stop; never abandon a live agent.
                }
                else { tabs.StopOwned(launch); }
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
}
