using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
    readonly ConcurrentDictionary<string, InteractiveLaunch> _liveSessions = new(StringComparer.Ordinal);

    public HerdrInteractiveBackend(HerdrTerminal terminal, InteractiveAgentKind kind, string stateRoot)
        : this(new HerdrAgentControl(terminal), new InteractiveTranscriptReader(), kind, stateRoot) { }

    internal HerdrInteractiveBackend(IHerdrAgentControl control, IInteractiveTranscriptReader transcripts, InteractiveAgentKind kind, string stateRoot)
    {
        _control = control;
        _transcripts = transcripts;
        _kind = kind;
        _stateRoot = stateRoot;
    }

    public IBackendRun Start(BackendRequest request)
    {
        var cwd = request.WorkingDirectory ?? Environment.CurrentDirectory;
        if (!Path.IsPathFullyQualified(cwd) || !Directory.Exists(cwd))
        {
            throw new BackendNotStartedException("interactive working directory does not exist");
        }

        var started = DateTimeOffset.UtcNow;
        if (request.ResumeSessionId is { } resumeId && _liveSessions.TryRemove(resumeId, out var live))
        {
            return new Run(_control, _transcripts, request, live, started, RememberSession);
        }

        var agentName = "atf" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10));
        var piDirectory = _kind == InteractiveAgentKind.Pi ? PiDirectory(request) : null;
        var launch = new InteractiveLaunch(_kind, agentName, cwd, request.ResumeSessionId, piDirectory,
            Path.Combine(_stateRoot, "herdr", agentName + ".bootstrap"))
        {
            Model = Option(request.Options, "model"),
            Effort = Option(request.Options, "effort"),
        };
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
        return new Run(_control, _transcripts, request, launch, started, RememberSession);
    }

    void RememberSession(string sessionId, InteractiveLaunch launch) => _liveSessions[sessionId] = launch;

    static string? Option(string options, string name) => options.Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).FirstOrDefault(pair => pair is [var key, { Length: > 0 }] && key == name)?[1];

    public bool StopIdleSession(string sessionId)
    {
        if (!_liveSessions.TryRemove(sessionId, out var launch))
        {
            return false;
        }
        try { _control.StopOwned(launch); }
        catch
        {
            _liveSessions.TryAdd(sessionId, launch);
            throw;
        }
        return true;
    }

    public void StopAllIdleSessions()
    {
        foreach (var sessionId in _liveSessions.Keys)
        {
            StopIdleSession(sessionId);
        }
    }

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
        InteractiveLaunch launch, DateTimeOffset started, Action<string, InteractiveLaunch> rememberSession) : IBackendRun
    {
        bool _delivered;
        bool _agentExited;
        readonly Lock _lifetime = new();
        bool _interrupted;
        bool _stopped;
        string? _sessionId = request.ResumeSessionId;
        string? _prompt;
        DateTimeOffset _lastPromptAt;
        int _promptAttempts;
        int _loggedMessages;
        public int? ProcessId => null; // The Herdr server owns the TUI process, not this daemon.

        public async Task DeliverAsync(CancellationToken cancellationToken)
        {
            var marker = "atf-corr:" + request.Correlation;
            var prompt = request.Instruction + "\n\n[AgentTeamForge correlation id: " + marker + " — internal marker, ignore this line]";
            try
            {
                await control.PromptAsync(launch, prompt, cancellationToken);
            }
            catch (HerdrLaunchException)
            {
                // Delivery is uncertain: report it as evidence (needs reconciliation) instead of
                // letting a Herdr control fault halt the whole dispatcher.
                return;
            }
            _prompt = prompt;
            _lastPromptAt = DateTimeOffset.UtcNow;
            _promptAttempts = 1;
            _delivered = true;
        }

        public async IAsyncEnumerable<BackendEvidence> ReadEvidenceAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (!_delivered)
            {
                yield return new BackendEvidence.ProtocolError("interactive_delivery_not_confirmed");
                yield break;
            }
            yield return new BackendEvidence.Ack(request.Correlation);
            var session = request.ResumeSessionId;
            if (session is not null)
            {
                yield return new BackendEvidence.Session(request.Correlation, session);
            }
            var sawWorking = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = await StatusAsync(cancellationToken);
                if (status is null)
                {
                    yield return new BackendEvidence.ProtocolError("interactive_control_failed");
                    yield break;
                }
                var output = transcripts.Read(launch, "atf-corr:" + request.Correlation, started);
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
                if (status == InteractiveAgentStatus.Working)
                {
                    sawWorking = true;
                }
                if (status is InteractiveAgentStatus.Done || (status == InteractiveAgentStatus.Idle && (sawWorking || output?.Message is not null)))
                {
                    if (output?.Message is { Length: > 0 } message && session is not null)
                    {
                        yield return new BackendEvidence.Result(request.Correlation, message);
                        yield return new BackendEvidence.EndOfOutput();
                        yield break;
                    }
                    // Herdr can classify completion before the native transcript flushes.
                }
                if (status == InteractiveAgentStatus.Blocked)
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
                if ((status is InteractiveAgentStatus.Done or InteractiveAgentStatus.Idle) && output is null &&
                    DateTimeOffset.UtcNow - _lastPromptAt >= TimeSpan.FromSeconds(5))
                {
                    if (_promptAttempts == 2)
                    {
                        yield return new BackendEvidence.ProtocolError("interactive_prompt_unobserved");
                        yield break;
                    }
                    if (!await RetryPromptAsync(cancellationToken))
                    {
                        yield return new BackendEvidence.ProtocolError("interactive_control_failed");
                        yield break;
                    }
                    _lastPromptAt = DateTimeOffset.UtcNow;
                    _promptAttempts++;
                }
                await Task.Delay(250, cancellationToken);
            }
        }

        async Task<InteractiveAgentStatus?> StatusAsync(CancellationToken cancellationToken)
        {
            try { return await control.StatusAsync(launch, cancellationToken); }
            catch (HerdrLaunchException) { return null; }
        }

        async Task<bool> RetryPromptAsync(CancellationToken cancellationToken)
        {
            try
            {
                await control.PromptAsync(launch, _prompt!, cancellationToken);
                return true;
            }
            catch (HerdrLaunchException) { return false; }
        }

        public void TerminateOwnedChild()
        {
            lock (_lifetime)
            {
                _stopped = true;
                control.StopOwned(launch);
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
                else
                {
                    control.StopOwned(launch);
                }
            }
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed record InteractiveLaunch(InteractiveAgentKind Kind, string AgentName, string WorkingDirectory,
    string? ResumeSessionId, string? PiSessionDirectory, string BootstrapPath)
{
    public string? Model { get; init; }
    public string? Effort { get; init; }
}

internal enum InteractiveAgentStatus { Idle, Working, Done, Blocked, Unknown, Gone }

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

internal sealed record InteractiveTranscript(string SessionId, string? Message, IReadOnlyList<string>? Messages = null, bool Completed = false)
{
    public IReadOnlyList<string> Progress => Messages ?? [];
}
