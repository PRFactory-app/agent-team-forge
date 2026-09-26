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
    readonly RetainedSessions _liveSessions;
    readonly TimeSpan _settleTimeout;

    public HerdrInteractiveBackend(HerdrTerminal terminal, InteractiveAgentKind kind, string stateRoot)
        : this(new HerdrAgentControl(terminal), new InteractiveTranscriptReader(terminal.Env), kind, stateRoot) { }

    // settleTimeout: how long an idle agent may go without native completion before the turn is uncertain.
    internal HerdrInteractiveBackend(IHerdrAgentControl control, IInteractiveTranscriptReader transcripts, InteractiveAgentKind kind, string stateRoot,
        TimeSpan? settleTimeout = null)
    {
        _control = control;
        _transcripts = transcripts;
        _kind = kind;
        _stateRoot = stateRoot;
        _liveSessions = new RetainedSessions(control.StopOwned);
        _settleTimeout = settleTimeout ?? TimeSpan.FromSeconds(60);
    }

    public IBackendRun Start(BackendRequest request)
    {
        var cwd = request.WorkingDirectory ?? Environment.CurrentDirectory;
        if (!Path.IsPathFullyQualified(cwd) || !Directory.Exists(cwd))
        {
            throw new BackendNotStartedException("interactive working directory does not exist");
        }

        var started = DateTimeOffset.UtcNow;
        if (request.ResumeSessionId is { } resumeId && _liveSessions.TryTake(resumeId, out var live))
        {
            var (model, effort) = InteractiveLaunch.Selection(request.Options);
            if (live.Model == model && live.Effort == effort)
            {
                return new Run(_control, _transcripts, request, live, started, _settleTimeout, RememberSession);
            }
            _control.StopOwned(live);
        }

        var agentName = "atf" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10));
        var piDirectory = _kind == InteractiveAgentKind.Pi ? PiDirectory(request) : null;
        var launch = new InteractiveLaunch(_kind, agentName, cwd, request.ResumeSessionId, piDirectory,
            Path.Combine(_stateRoot, "herdr", agentName + ".bootstrap")).WithSelection(request.Options);
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
        return new Run(_control, _transcripts, request, launch, started, _settleTimeout, RememberSession);
    }

    void RememberSession(string sessionId, InteractiveLaunch launch) => _liveSessions.Remember(sessionId, launch);

    public bool StopIdleSession(string sessionId) => _liveSessions.Stop(sessionId);

    public void StopAllIdleSessions() => _liveSessions.StopAll();

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
        InteractiveLaunch launch, DateTimeOffset started, TimeSpan settleTimeout, Action<string, InteractiveLaunch> rememberSession) : IBackendRun
    {
        bool _delivered;
        bool _agentExited;
        readonly Lock _lifetime = new();
        bool _interrupted;
        bool _stopped;
        string? _sessionId = request.ResumeSessionId;
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
            // Submitted exactly once. A missing transcript never proves non-delivery, so the
            // prompt is never resent; an unproven turn ends as needs_reconciliation instead.
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
            // Only the native completion record of this correlated turn completes the job;
            // Herdr's idle/done classification alone can follow interim commentary.
            var quietSince = DateTimeOffset.UtcNow;
            var seenMessages = 0;
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
                if (output is { Completed: true, Message: { Length: > 0 } message } && session is not null)
                {
                    yield return new BackendEvidence.Result(request.Correlation, message);
                    yield return new BackendEvidence.EndOfOutput();
                    yield break;
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
                var progressed = output is not null && output.Progress.Count != seenMessages;
                seenMessages = output?.Progress.Count ?? 0;
                if (status is not (InteractiveAgentStatus.Idle or InteractiveAgentStatus.Done) || progressed)
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
