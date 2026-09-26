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
            || !AcceptJob.ValidOption(request.Model) || !AcceptJob.ValidOption(request.Effort)
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
            // A headless sibling may have ended before recording its own session.
            // Reconcile only terminal runs with owned process evidence, never TUIs.
            foreach (var peerId in store.GetSessionJobs(parent.JobId))
            {
                if (store.GetJob(peerId) is not { } peer) { continue; } // Concurrent prune.
                var peerRuns = store.GetRuns(peerId);
                if (peer.Status is JobStatus.NeedsReconciliation or JobStatus.Cancelled && peerRuns.Count > 0
                    && peerRuns[^1].BackendPid is not null && peerRuns.All(r => r.State != "started")
                    && !OrphanedBackendProcess.HasMarkedProcess([.. peerRuns.Select(r => r.Correlation)]))
                {
                    store.ReconcileStoppedJob(peerId);
                }
            }
            if (store.IsSessionFenced(parent.JobId))
            {
                return JobResult.Fail(JobErrors.ParentNotReady);
            }
            // A needs_reconciliation row can be committed before its child exits. Only
            // a terminal run with no live marked process proves this session is idle.
            // A run without a daemon-owned pid (a Herdr TUI, or a start that never
            // reported) carries no marker we can scan, so it proves nothing.
            if (runs.Any(r => r.State == "started")
                || (parent.Status is JobStatus.Failed or JobStatus.NeedsReconciliation
                    && (runs.Count == 0
                        || OrphanedBackendProcess.HasMarkedProcess([.. runs.Select(r => r.Correlation)]))))
            {
                return JobResult.Fail(JobErrors.ParentNotReady);
            }
        }
        catch (StorageException ex)
        {
            return JobResult.Fail(JobErrors.FromStorage(ex));
        }

        // A new turn keeps the parent's concrete selection unless the caller changes it.
        var inheritedModel = JobOptions.Read(parent.Options, "model");
        var inheritedEffort = JobOptions.Read(parent.Options, "effort");
        (string? model, string? effort) selection;
        try
        {
            selection = request.Model is null && request.Effort is null
                ? (inheritedModel, inheritedEffort)
                : accept.ResolveModel(parent.Backend, request.Model ?? inheritedModel, request.Effort ?? inheritedEffort);
        }
        catch (ArgumentException ex)
        {
            return JobResult.Fail(ex.Message);
        }
        var options = "behavior=complete;hold=0" + (request.Interrupt ? ";interrupt=1" : "");
        if (selection.model is not null)
        {
            options += ";model=" + selection.model;
        }
        if (selection.effort is not null)
        {
            options += ";effort=" + selection.effort;
        }
        return accept.Admit(Operation, request.IdempotencyKey, request.Instruction, options,
            parent.Backend, parent.Cwd, parent.JobId, request.WakeKey, request.WakeGeneration, worktreeBase: parent.WorktreeBase,
            worktreePath: parent.WorktreePath, worktreeBranch: parent.WorktreeBranch,
            timeoutSeconds: request.TimeoutSeconds, queueTtlSeconds: request.QueueTtlSeconds,
            interruptParent: interruptRunning, cancelRunning: cancelRunning, leadSessionId: request.LeadSessionId,
            targetAgent: parent.TargetAgent);
    }
}
