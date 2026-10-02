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
public sealed class ListJobs(JobStore store, BoundPrincipal principal, JobLogs? logs = null, bool interactiveLaunch = false)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    public JobListResult Execute(ListJobsRequest request)
    {
        var limit = request.Limit ?? DefaultPageSize;
        var invalid = limit is < 1 or > MaxPageSize ? $"Invalid limit: must be an integer from 1 to {MaxPageSize}."
            : request.Status is not (null or JobStatus.Queued or JobStatus.Running or JobStatus.Completed or JobStatus.Failed or JobStatus.NeedsReconciliation or JobStatus.Cancelled)
                ? "Invalid status: must be one of: queued, running, completed, failed, needs_reconciliation, cancelled."
            : request.Backend is not (null or "fake" or "claude" or "codex" or "pi" or "cursor" or "droid")
                ? "Invalid backend: must be one of: fake, claude, codex, pi, cursor, droid."
            : request.Since is not null && !DateTimeOffset.TryParse(request.Since, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
                ? "Invalid since: must be an ISO 8601 time such as 2026-01-31T12:00:00Z."
            : request.Cursor is not null && (request.Cursor.Length > 64 || !request.Cursor.StartsWith("job_", StringComparison.Ordinal))
                ? "Invalid cursor: must be the next_cursor of a previous page."
            : null;
        if (invalid is not null)
        {
            return new JobListResult(null, JobErrors.InvalidRequest) { Detail = invalid };
        }

        IReadOnlyList<JobSummaryRecord> rows;
        try
        {
            // One extra row decides truncation without a separate count query.
            // unread=true is "everything I have not read", so a since window must not hide it.
            var since = request.Since is null || request.Unread ? null : DateTimeOffset.Parse(request.Since, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime().ToString("O");
            rows = store.ListJobs(principal.Principal, principal.Team, request.Status, request.Backend, since, request.Cursor, limit + 1,
                request.LeadSessionId, request.AllWorkspace ? request.Workspace : null, request.OrderByActivity, request.IncludeConnector, request.Unread, request.ExcludeArchived);
        }
        catch (StorageException ex)
        {
            return new JobListResult(null, ex.Failure == StorageFailure.Busy ? JobErrors.StorageBusy : JobErrors.StorageUnavailable);
        }

        var hasMore = rows.Count > limit;
        var jobs = rows.Take(limit).Select(r => new JobSummary(r.JobId, r.Status, r.ReasonCode, r.Attempts, r.AcceptedAt, r.UpdatedAt)
        {
            Startup = StartupProgress.Read(store, r.JobId, r.Status, r.Backend, r.ReasonCode, interactiveLaunch),
            WorktreePath = r.WorktreePath,
            WorktreeBranch = r.WorktreeBranch,
            Backend = r.Backend,
            Cwd = r.Cwd,
            Model = JobOptions.Read(r.Options ?? "", "model"),
            Effort = JobOptions.Read(r.Options ?? "", "effort"),
            HerdrPlacement = JobOptions.Read(r.Options ?? "", "herdr_placement"),
            SessionId = r.SessionId,
            ParentJobId = r.ParentJobId,
            LeadSessionId = r.LeadSessionId,
            LeadWorkspace = r.LeadWorkspace,
            LeadName = r.LeadName,
            TargetAgent = r.TargetAgent,
            Connector = r.Connector,
            WorkItemId = r.WorkItemId,
            Unread = r.Unread,
            Archived = r.Archived,
            Revision = r.Revision,
            LastActivity = logs?.LastActivity(r.JobId, r.Backend ?? ""),
        }).ToList();
        return new JobListResult(new JobListPage(jobs, limit, hasMore, hasMore ? jobs[^1].JobId : null), null);
    }
}

/// <summary>Optional exact status filter, page size and opaque continuation cursor; no other query surface.</summary>
public sealed record ListJobsRequest(string? Status = null, int? Limit = null, string? Cursor = null, string? Backend = null, string? Since = null)
{
    public string? LeadSessionId { get; init; }
    public bool AllWorkspace { get; init; }
    public string? Workspace { get; init; }
    public bool OrderByActivity { get; init; }
    public bool IncludeConnector { get; init; }
    public bool Unread { get; init; }
    /// <summary>Console only: hide jobs archived by "clear finished". MCP/CLI listings leave this off.</summary>
    public bool ExcludeArchived { get; init; }
}

/// <summary>Inspection view of a job; use job_get for its result.</summary>
public sealed record JobSummary(string JobId, string Status, string? ReasonCode, int Attempts, string AcceptedAt, string UpdatedAt)
{
    public bool? AgentLive { get; init; }
    public StartupProgress? Startup { get; init; }
    public string? Backend { get; init; }
    public string? Cwd { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? HerdrPlacement { get; init; }
    public string? HerdrSession { get; init; }
    public string? HerdrTab { get; init; }
    public string? HerdrTabLabel { get; init; }
    public string? SessionId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AgentTeamForge.Business.Features.Usage.TokenUsage? SessionTokens { get; init; }
    public string? ParentJobId { get; init; }
    public string? WorktreePath { get; init; }
    public string? WorktreeBranch { get; init; }
    public string? LeadSessionId { get; init; }
    public string? LeadName { get; init; }
    public string? LeadWorkspace { get; init; }
    public string? TargetAgent { get; init; }
    /// <summary>PRFactory work item of a connector job; null for ordinary jobs.</summary>
    public string? WorkItemId { get; init; }
    public bool Connector { get; init; }
    public bool Unread { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Archived { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public long Revision { get; init; }
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

public sealed record JobListResult(JobListPage? Page, string? Error)
{
    public string? Detail { get; init; }
}
