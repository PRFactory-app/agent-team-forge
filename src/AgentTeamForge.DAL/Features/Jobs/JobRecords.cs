namespace AgentTeamForge.DAL.Features.Jobs;

public static class JobStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string NeedsReconciliation = "needs_reconciliation";
}

public sealed record NewJob(
    string Principal,
    string Team,
    string TargetAgent,
    string Operation,
    string IdempotencyKey,
    string Fingerprint,
    string Instruction,
    string Options)
{
    public string Backend { get; init; } = "fake";

    public string? Cwd { get; init; }

    public string? ParentJobId { get; init; }
    public bool InterruptParent { get; init; }

    public string? WorktreePath { get; init; }
    public string? WorktreeBranch { get; init; }
    public string? WorktreeBase { get; init; }
    public bool CreateWorktree { get; init; }

    /// <summary>Running time allowed once an attempt starts; null means no job timeout.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>Queued time allowed from the acceptance commit; null means no queue TTL.</summary>
    public int? QueueTtlSeconds { get; init; }

    public string? LeadSessionId { get; init; }

    public string? WakeTargetKey { get; init; }

    public long? WakeGeneration { get; init; }
}

public sealed record JobRecord(
    string JobId,
    string Principal,
    string Team,
    string TargetAgent,
    string IdempotencyKey,
    string Instruction,
    string Options,
    string Status,
    string? ReasonCode,
    string? ResultText,
    int Attempts,
    string Backend,
    string? Cwd,
    string? ParentJobId,
    string? SessionId)
{
    public string? WorktreePath { get; init; }
    public string? WorktreeBranch { get; init; }
    public string? WorktreeBase { get; init; }
    public int? TimeoutSeconds { get; init; }
}

public sealed record Accepted(JobRecord Job, string? InterruptedJobId = null);
public sealed record Existing(JobRecord Job);
public sealed record Conflict;
public sealed record QueueFull;
public sealed record ParentNotReady;
public sealed record ParentNotFound;

public readonly union AcceptOutcome(Accepted, Existing, Conflict, QueueFull, ParentNotReady, ParentNotFound);

/// <summary>A committed attempt-start: generation and correlation exist before any backend effect.</summary>
public sealed record AttemptClaim(JobRecord Job, string RunId, long Generation, string Correlation);

public sealed record RunRef(string JobId, string RunId, long Generation, string Correlation);

public sealed record CancelOutcome(JobRecord? Job, bool WasRunning, bool Changed);

public sealed record EventRecord(long Seq, string JobId, string? RunId, string Kind);

public sealed record RunRecord(string RunId, long Generation, string Correlation, string State, bool Acked, int? BackendPid, string? ReasonCode);

/// <summary>Read-only inspection row: committed state only, no instruction or result payload.</summary>
public sealed record JobSummaryRecord(string JobId, string Status, string? ReasonCode, int Attempts, string AcceptedAt, string UpdatedAt)
{
    public string? Options { get; init; }
    public string? Backend { get; init; }
    public string? Cwd { get; init; }
    public string? SessionId { get; init; }
    public string? ParentJobId { get; init; }
    public string? WorktreePath { get; init; }
    public string? WorktreeBranch { get; init; }
    public string? LeadSessionId { get; init; }
    public string? LeadWorkspace { get; init; }
    public string? TargetAgent { get; init; }
}
