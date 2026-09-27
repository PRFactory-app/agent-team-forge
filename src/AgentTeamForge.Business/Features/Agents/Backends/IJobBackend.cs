namespace AgentTeamForge.Business.Features.Agents.Backends;

public sealed record BackendRequest(string JobId, string Correlation, string Instruction, string Options)
{
    public Action<string>? StartupProgress { get; init; }
    public string? ManagedMcpConfig { get; init; }
    /// <summary>Native session to resume for a follow-up; null starts a new session.</summary>
    public string? ResumeSessionId { get; init; }

    /// <summary>Working directory for the agent; null uses the daemon default.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Receives each raw stdout/stderr read as soon as it arrives.</summary>
    public Action<string, ReadOnlyMemory<byte>>? Output { get; init; }
}

/// <summary>Evidence observed from a backend; Business alone decides what it means.</summary>
public abstract record BackendEvidence
{
    public sealed record Ack(string Correlation) : BackendEvidence;

    public sealed record Session(string Correlation, string SessionId) : BackendEvidence;

    public sealed record Result(string Correlation, string Output) : BackendEvidence;

    /// <summary>The agent ended its turn with a reported API failure.</summary>
    public sealed record AgentError(string Code, string Details) : BackendEvidence;

    public sealed record ProtocolError(string Code) : BackendEvidence;

    /// <summary>CLI rejected its arguments before a turn began; no job effect occurred.</summary>
    public sealed record NotStarted(string Details) : BackendEvidence;

    /// <summary>The owned interactive wrapper exited before the agent acknowledged this turn.</summary>
    public sealed record LaunchFailed(string Details) : BackendEvidence;

    public sealed record EndOfOutput : BackendEvidence;
}

/// <summary>Thrown only when the backend provably never started or its owned launch effects were fully cleaned up.</summary>
public sealed class BackendNotStartedException(string message, Exception? inner = null) : Exception(message, inner);

public interface IJobBackend
{
    /// <summary>
    /// Starts the backend without delivering the request. Throws
    /// <see cref="BackendNotStartedException"/> only if nothing started or owned launch effects were fully cleaned up. OS
    /// process start is not cancellable; the caller bounds how long it waits
    /// and terminates a run that returns after its deadline.
    /// </summary>
    IBackendRun Start(BackendRequest request);
}

public interface IBackendRun : IAsyncDisposable
{
    int? ProcessId { get; }

    /// <summary>Whether this run's held process handle still identifies a live child.</summary>
    bool OwnedChildAlive => false;

    /// <summary>Positive proof that this run's owned interactive session was stopped.</summary>
    bool OwnedSessionStopped => false;

    /// <summary>
    /// Delivers the request given to Start. May not observe cancellation
    /// promptly (a blocked pipe write); once called, delivery is uncertain.
    /// </summary>
    Task DeliverAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<BackendEvidence> ReadEvidenceAsync(CancellationToken cancellationToken);

    /// <summary>Terminates the process tree of the child this run started, via its held handle.</summary>
    void TerminateOwnedChild();

    /// <summary>Interrupts this turn. Headless runs terminate; interactive runs may keep their agent alive.</summary>
    void InterruptTurn() => TerminateOwnedChild();
}
