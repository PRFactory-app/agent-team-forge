using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

public enum InteractiveAgentKind { Claude, Codex, Pi }

/// <summary>Runs a real agent TUI in a tab of an ATF-owned Herdr session.</summary>
public sealed class HerdrInteractiveBackend : IJobBackend
{
    readonly IHerdrAgentControl _control;
    readonly IInteractiveTranscriptReader _transcripts;
    readonly InteractiveAgentKind _kind;
    readonly string _stateRoot;

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

        var agentName = "atf" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10));
        var started = DateTimeOffset.UtcNow;
        var piDirectory = _kind == InteractiveAgentKind.Pi ? PiDirectory(request) : null;
        var launch = new InteractiveLaunch(_kind, agentName, cwd, request.ResumeSessionId, piDirectory,
            Path.Combine(_stateRoot, "herdr", agentName + ".bootstrap"));
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
        return new Run(_control, _transcripts, request, launch, started);
    }

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
        InteractiveLaunch launch, DateTimeOffset started) : IBackendRun
    {
        bool _delivered;
        bool _completed;
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
                        _completed = true;
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
                    _completed = true; // Nothing left to observe; close our owned session on dispose.
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

        public void TerminateOwnedChild() => control.StopOwned(launch);

        public ValueTask DisposeAsync()
        {
            // A settled turn is finished; close only our proven session before a
            // follow-up resumes its native session in a fresh owned tab.
            if (_completed)
            {
                control.StopOwned(launch);
            }
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed record InteractiveLaunch(InteractiveAgentKind Kind, string AgentName, string WorkingDirectory,
    string? ResumeSessionId, string? PiSessionDirectory, string BootstrapPath);

internal enum InteractiveAgentStatus { Idle, Working, Done, Blocked, Unknown, Gone }

internal interface IHerdrAgentControl
{
    Task StartAsync(InteractiveLaunch launch, CancellationToken cancellationToken);
    Task PromptAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken);
    Task<InteractiveAgentStatus> StatusAsync(InteractiveLaunch launch, CancellationToken cancellationToken);
    void StopOwned(InteractiveLaunch launch);
}

internal interface IInteractiveTranscriptReader
{
    InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started);
    string? FindPiSessionDirectory(string root, string sessionId);
}

internal sealed record InteractiveTranscript(string SessionId, string? Message, IReadOnlyList<string>? Messages = null)
{
    public IReadOnlyList<string> Progress => Messages ?? [];
}
