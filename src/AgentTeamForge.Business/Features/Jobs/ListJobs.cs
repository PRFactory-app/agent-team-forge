using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using System.Globalization;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>
/// Authorized, output-bounded, read-only inspection of the bound principal/team's jobs.
/// Paging is best-effort live keyset: rows that keep matching are never duplicated,
/// while jobs accepted or changing status during a scan may be included or missed.
/// Reports committed state only: `running` means an attempt-start is committed,
/// not that a live process was observed. Never dispatches, acknowledges or resends.
/// </summary>
public sealed class ListJobs(JobStore store, BoundPrincipal principal, JobLogs? logs = null)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    public JobListResult Execute(ListJobsRequest request)
    {
        var limit = request.Limit ?? DefaultPageSize;
        if (limit is < 1 or > MaxPageSize
            || request.Status is not (null or JobStatus.Queued or JobStatus.Running or JobStatus.Completed or JobStatus.Failed or JobStatus.NeedsReconciliation or JobStatus.Cancelled)
            || request.Backend is not (null or "fake" or "claude" or "codex" or "pi")
            || (request.Since is not null && !DateTimeOffset.TryParse(request.Since, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            || (request.Cursor is not null && (request.Cursor.Length > 64 || !request.Cursor.StartsWith("job_", StringComparison.Ordinal))))
        {
            return new JobListResult(null, JobErrors.InvalidRequest);
        }

        IReadOnlyList<JobSummaryRecord> rows;
        try
        {
            // One extra row decides truncation without a separate count query.
            var since = request.Since is null ? null : DateTimeOffset.Parse(request.Since, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime().ToString("O");
            rows = store.ListJobs(principal.Principal, principal.Team, request.Status, request.Backend, since, request.Cursor, limit + 1,
                request.LeadSessionId, request.AllWorkspace ? request.Workspace : null, request.OrderByActivity);
        }
        catch (StorageException ex)
        {
            return new JobListResult(null, ex.Failure == StorageFailure.Busy ? JobErrors.StorageBusy : JobErrors.StorageUnavailable);
        }

        var hasMore = rows.Count > limit;
        var jobs = rows.Take(limit).Select(r => new JobSummary(r.JobId, r.Status, r.ReasonCode, r.Attempts, r.AcceptedAt, r.UpdatedAt)
        {
            WorktreePath = r.WorktreePath,
            WorktreeBranch = r.WorktreeBranch,
            Backend = r.Backend,
            SessionId = r.SessionId,
            ParentJobId = r.ParentJobId,
            LeadSessionId = r.LeadSessionId,
            TargetAgent = r.TargetAgent,
            Model = Option(r.Options, "model"),
            Effort = Option(r.Options, "effort"),
            LastActivity = logs?.LastActivity(r.JobId, r.Backend ?? ""),
        }).ToList();
        return new JobListResult(new JobListPage(jobs, limit, hasMore, hasMore ? jobs[^1].JobId : null), null);
    }

    static string? Option(string? options, string name) => options?.Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .FirstOrDefault(part => part is [var key, { Length: > 0 }] && key == name)?[1];
}

/// <summary>Optional exact status filter, page size and opaque continuation cursor; no other query surface.</summary>
public sealed record ListJobsRequest(string? Status = null, int? Limit = null, string? Cursor = null, string? Backend = null, string? Since = null)
{
    public string? LeadSessionId { get; init; }
    public bool AllWorkspace { get; init; }
    public string? Workspace { get; init; }
    public bool OrderByActivity { get; init; }
}

/// <summary>Inspection view of a job; use job_get for its result.</summary>
public sealed record JobSummary(string JobId, string Status, string? ReasonCode, int Attempts, string AcceptedAt, string UpdatedAt)
{
    public string? Backend { get; init; }
    public string? SessionId { get; init; }
    public string? ParentJobId { get; init; }
    public string? WorktreePath { get; init; }
    public string? WorktreeBranch { get; init; }
    public string? LeadSessionId { get; init; }
    public string? TargetAgent { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string Light => Status switch
    {
        JobStatus.Queued or "waiting" or "parked" => "yellow",
        JobStatus.Running or "working" or "idle" => "green",
        JobStatus.Completed or "succeeded" or "done" => "grey",
        JobStatus.Cancelled or "stopped" => "grey",
        _ => "red",
    };
    public string? LastActivity { get; init; }
}

/// <summary>One page in the requested order. `NextCursor` is set exactly when `HasMore` is true.</summary>
public sealed record JobListPage(IReadOnlyList<JobSummary> Jobs, int Limit, bool HasMore, string? NextCursor);

public sealed record JobListResult(JobListPage? Page, string? Error);
