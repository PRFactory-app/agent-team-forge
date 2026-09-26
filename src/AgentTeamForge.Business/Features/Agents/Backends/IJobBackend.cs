namespace AgentTeamForge.Business.Features.Agents.Backends;

public sealed record BackendRequest(string JobId, string Correlation, string Instruction, string Options);

/// <summary>Evidence observed from a backend; Business alone decides what it means.</summary>
public abstract record BackendEvidence
{
    public sealed record Ack(string Correlation) : BackendEvidence;

    public sealed record Result(string Correlation, string Output) : BackendEvidence;

    public sealed record ProtocolError(string Code) : BackendEvidence;

    public sealed record EndOfOutput : BackendEvidence;
}

/// <summary>Thrown only when the backend provably never started (no effect possible).</summary>
public sealed class BackendNotStartedException(string message, Exception? inner = null) : Exception(message, inner);

public interface IJobBackend
{
    /// <summary>
    /// Starts the backend without delivering the request. Throws
    /// <see cref="BackendNotStartedException"/> only if nothing started. OS
    /// process start is not cancellable; the caller bounds how long it waits
    /// and terminates a run that returns after its deadline.
    /// </summary>
    IBackendRun Start(BackendRequest request);
}

public interface IBackendRun : IAsyncDisposable
{
    int? ProcessId { get; }

    /// <summary>
    /// Delivers the request given to Start. May not observe cancellation
    /// promptly (a blocked pipe write); once called, delivery is uncertain.
    /// </summary>
    Task DeliverAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<BackendEvidence> ReadEvidenceAsync(CancellationToken cancellationToken);

    /// <summary>Terminates only the direct child this run started, via its held handle.</summary>
    void TerminateOwnedChild();
}
