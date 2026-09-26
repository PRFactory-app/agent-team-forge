using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>
/// Accepts a new job that resumes the parent's recorded native session on the
/// parent's backend and cwd. An interrupt cancels a running parent in the same
/// transaction that accepts the new turn.
/// </summary>
public sealed class FollowUpJob(JobStore store, BoundPrincipal principal, AcceptJob accept, Action<string>? cancelRunning = null)
{
    public const string Operation = "job_follow_up";

    public JobResult Execute(FollowUpRequest request)
    {
        if (!accept.IsValid(request.IdempotencyKey, request.Instruction)
            || !AcceptJob.ValidLimits(request.TimeoutSeconds, request.QueueTtlSeconds)
            || string.IsNullOrWhiteSpace(request.ParentJobId) || request.ParentJobId.Length > 64)
        {
            return JobResult.Fail(JobErrors.InvalidRequest);
        }

        JobRecord? parent;
        try
        {
            parent = store.GetJob(request.ParentJobId);
        }
        catch (StorageException ex)
        {
            return JobResult.Fail(JobErrors.FromStorage(ex));
        }

        if (parent is null || parent.Principal != principal.Principal || parent.Team != principal.Team)
        {
            return JobResult.Fail(JobErrors.NotFound);
        }

        // Resuming a session that is still in a turn would race the running agent;
        // only an interrupt may target a running parent (it is cancelled atomically).
        var interruptRunning = request.Interrupt && parent.Status == JobStatus.Running;
        if (parent.SessionId is null || parent.Status is not (JobStatus.Completed or JobStatus.Cancelled or JobStatus.Failed or JobStatus.NeedsReconciliation)
            && !interruptRunning)
        {
            return JobResult.Fail(JobErrors.ParentNotReady);
        }

        if (interruptRunning && cancelRunning is null)
        {
            return JobResult.Fail(JobErrors.DaemonUnhealthy);
        }

        try
        {
            var runs = interruptRunning ? [] : store.GetRuns(parent.JobId);
            // A needs_reconciliation row can be committed before its child exits. Only
            // a terminal run with no live marked process proves this session is idle.
            // A run without a daemon-owned pid (a Herdr TUI, or a start that never
            // reported) carries no marker we can scan, so it proves nothing.
            if (runs.Any(r => r.State == "started")
                || (parent.Status is JobStatus.Failed or JobStatus.NeedsReconciliation
                    && (runs.Count == 0
                        || (parent.Status == JobStatus.NeedsReconciliation && runs[^1].BackendPid is null)
                        || OrphanedBackendProcess.HasMarkedProcess([.. runs.Select(r => r.Correlation)]))))
            {
                return JobResult.Fail(JobErrors.ParentNotReady);
            }
        }
        catch (StorageException ex)
        {
            return JobResult.Fail(JobErrors.FromStorage(ex));
        }

        return accept.Admit(Operation, request.IdempotencyKey, request.Instruction,
            "behavior=complete;hold=0" + (request.Interrupt ? ";interrupt=1" : ""),
            parent.Backend, parent.Cwd, parent.JobId, request.WakeKey, request.WakeGeneration, worktreeBase: parent.WorktreeBase,
            worktreePath: parent.WorktreePath, worktreeBranch: parent.WorktreeBranch,
            timeoutSeconds: request.TimeoutSeconds, queueTtlSeconds: request.QueueTtlSeconds,
            interruptParent: interruptRunning, cancelRunning: cancelRunning);
    }
}
