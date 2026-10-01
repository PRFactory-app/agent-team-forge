using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;

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

public sealed record SubmitJobRequest(string IdempotencyKey, string Instruction, string? Behavior, bool Hold)
{
    /// <summary>Backend name from <see cref="Agents.Backends.BackendCatalog"/>; null means fake.</summary>
    public string? Backend { get; init; }
    public string? TargetAgent { get; init; }
    public string[]? ExpectedOutputs { get; init; }

    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? HerdrPlacement { get; init; }

    /// <summary>Absolute existing directory the agent runs in; null uses the daemon default.</summary>
    public string? Cwd { get; init; }

    public bool Worktree { get; init; }

    /// <summary>Cancel the job with reason <c>timeout</c> this long after its attempt starts.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>Cancel the job with reason <c>queue_ttl</c> if it has not started this long after acceptance.</summary>
    public int? QueueTtlSeconds { get; init; }

    /// <summary>Registered wake target of the submitting bridge; bound in the accept transaction.</summary>
    public string? LeadSessionId { get; init; }
    public string? WakeKey { get; init; }
    public long? WakeGeneration { get; init; }
}

/// <summary>A new turn in the parent job's native session, on the same backend and cwd.</summary>
public sealed record FollowUpRequest(string ParentJobId, string Instruction, string IdempotencyKey)
{
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public bool Interrupt { get; init; }
    public bool Defer { get; init; }
    public bool ReplaceIfIdle { get; init; } = true;
    public int? TimeoutSeconds { get; init; }
    public int? QueueTtlSeconds { get; init; }

    public string? LeadSessionId { get; init; }
    public string? WakeKey { get; init; }
    public long? WakeGeneration { get; init; }
}

/// <summary>Stable machine-readable error codes; English messages are not contract.</summary>
public static class JobErrors
{
    public const string InvalidRequest = "invalid_request";
    public const string InstructionTooLong = "instruction_too_long";
    public const string HerdrPromptTooLarge = "herdr_prompt_exceeds_120k_utf8_bytes";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string QueueFull = "queue_full";
    public const string StorageBusy = "storage_busy";
    public const string StorageUnavailable = "storage_unavailable";
    public const string NotFound = "not_found";
    public const string DaemonUnhealthy = "daemon_unhealthy";
    public const string BackendUnavailable = "backend_unavailable";
    public const string OwnershipNotProven = "owned_agent_not_verified";
    public const string ParentNotReady = "parent_not_ready";
    public const string SessionExpired = "session_expired";
    public const string CwdNotGitRepo = "cwd_not_git_repo";
    public const string NoWorktree = "no_worktree";

    public static string FromStorage(StorageException ex) => ex.Failure == StorageFailure.Busy ? StorageBusy : StorageUnavailable;

    /// <summary>Names the job blocking a follow-up: the session's fencing peer if any, else the parent itself.</summary>
    public static string ParentNotReadyDetail(JobStore store, string parentJobId)
    {
        if (store.FencingPeer(parentJobId) is { } peer)
        {
            return $"Session fenced by {peer.JobId} ({peer.Status}{(peer.ReasonCode is null ? "" : ": " + peer.ReasonCode)}); stop_job {peer.JobId}, then retry.";
        }
        return $"Parent {parentJobId} is not idle; wait for it to finish or stop_job {parentJobId}, then retry.";
    }

    public static string StorageDetail(StorageException ex) => ex.Failure == StorageFailure.Busy ? "Database busy; retry." : "Job database unavailable.";
}

/// <summary>The public view of a job. Never a raw storage record.</summary>
public sealed record JobView(string JobId, string Status, string? Result, string? ReasonCode, int Attempts)
{
    public string[]? ExpectedOutputs { get; init; }
    public bool? AgentLive { get; init; }
    public JobDelivery? Delivery { get; init; }
    public StartupProgress? Startup { get; init; }
    public string? Backend { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? HerdrPlacement { get; init; }
    public string? HerdrSession { get; init; }
    public string? HerdrTab { get; init; }
    public string? HerdrTabLabel { get; init; }

    /// <summary>Raw instruction of this turn; only the single-job read fills it (web console), never lists or action responses.</summary>
    public string? Instruction { get; init; }

    public string? SessionId { get; init; }

    public string? ParentJobId { get; init; }

    public string? Cwd { get; init; }

    public string? WorktreePath { get; init; }
    public string? WorktreeBranch { get; init; }
}

public sealed record JobDelivery(string State, string? RunId, string? SubmittedAt, string? AcknowledgedAt)
{
    public string? NativeSubmissionId { get; init; }
}

public sealed record JobResult(JobView? Job, string? Outcome, string? Error)
{
    public IReadOnlyList<JobView>? Jobs { get; init; }

    public string? Detail { get; init; }

    public static JobResult Ok(JobView job, string outcome) => new(job, outcome, null);

    public static JobResult Fail(string error, string? detail = null) => new(null, null, error) { Detail = detail };
}
