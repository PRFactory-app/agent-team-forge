namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Deterministic fake-backend behaviours (spike test profile surface).</summary>
public static class FakeBehavior
{
    public const string Complete = "complete";
    public const string EofAfterAck = "eof_after_ack";
    public const string ExitAfterReceipt = "exit_after_receipt";
    public const string MismatchedCorrelation = "mismatched_correlation";
    public const string Hang = "hang";
    public const string StallBeforeRead = "stall_before_read";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Complete, EofAfterAck, ExitAfterReceipt, MismatchedCorrelation, Hang, StallBeforeRead };
}

public sealed record SubmitJobRequest(string IdempotencyKey, string Instruction, string? Behavior, bool Hold);

/// <summary>Stable machine-readable error codes; English messages are not contract.</summary>
public static class JobErrors
{
    public const string InvalidRequest = "invalid_request";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string QueueFull = "queue_full";
    public const string StorageBusy = "storage_busy";
    public const string StorageUnavailable = "storage_unavailable";
    public const string NotFound = "not_found";
    public const string DaemonUnhealthy = "daemon_unhealthy";
}

/// <summary>The public view of a job. Never a raw storage record.</summary>
public sealed record JobView(string JobId, string Status, string? Result, string? ReasonCode, int Attempts);

public sealed record JobResult(JobView? Job, string? Outcome, string? Error)
{
    public static JobResult Ok(JobView job, string outcome) => new(job, outcome, null);

    public static JobResult Fail(string error) => new(null, null, error);
}
