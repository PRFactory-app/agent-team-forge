using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Business.Features.Agents.Backends;
using System.Text;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>
/// Accepts a new job that resumes the parent's recorded native session on the
/// parent's backend and cwd. An interrupt cancels a running parent in the same
/// transaction that accepts the new turn.
/// </summary>
public sealed class FollowUpJob(JobStore store, BoundPrincipal principal, AcceptJob accept, Action<string>? cancelRunning = null,
    Func<int, byte[]>? readProcessEnvironment = null, Func<JobRecord, bool>? reconcileIdleInteractive = null)
{
    public const string Operation = "job_follow_up";

    public JobResult Execute(FollowUpRequest request)
    {
        if (request.Instruction is { } instruction && accept.InstructionError(instruction, null) is { } instructionError)
        {
            return JobResult.Fail(instructionError);
        }
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

        if (accept.InstructionError(request.Instruction, parent.Backend) is { } deliveryError)
        {
            return JobResult.Fail(deliveryError);
        }

        var nativeCodex = parent.Backend == BackendCatalog.Codex && parent.SessionId is not null
            && !request.Interrupt && request.ReplaceIfIdle && Encoding.UTF8.GetByteCount(request.Instruction) <= 16 * 1024
            && request.Model is null && request.Effort is null;
        // Leave room for the fixed correlation marker in Claude's 16 KiB inbox line.
        var nativeClaude = parent.Backend == BackendCatalog.Claude && parent.SessionId is not null
            && !request.Interrupt && request.ReplaceIfIdle && Encoding.UTF8.GetByteCount(request.Instruction) <= 16 * 1024 - 256
            && request.Model is null && request.Effort is null;
        var defer = request.Defer;

        var interruptRunning = request.Interrupt && parent.Status == JobStatus.Running;
        try
        {
            // Retry lookup must precede mutable readiness checks: accepted work may
            // now be running, or fenced after a daemon crash. Admit still checks the fingerprint.
            if (!store.HasAcceptedKey(principal.Principal, principal.Team, Operation,
                request.LeadSessionId is null ? request.IdempotencyKey : request.LeadSessionId + ":" + request.IdempotencyKey))
            {
                // Resuming a session that is still in a turn would race the running agent;
                // defer waits for its terminal state; interrupt cancels it atomically.
                var deferred = defer && !request.Interrupt && parent.Status is JobStatus.Queued or JobStatus.Running;
                if (!deferred && (parent.SessionId is null || parent.Status is not (JobStatus.Completed or JobStatus.Cancelled or JobStatus.Failed or JobStatus.NeedsReconciliation)
                    && !interruptRunning))
                {
                    return JobResult.Fail(JobErrors.ParentNotReady);
                }

                if (interruptRunning && cancelRunning is null)
                {
                    return JobResult.Fail(JobErrors.DaemonUnhealthy);
                }

                var runs = interruptRunning || deferred ? [] : store.GetRuns(parent.JobId);
                // A headless sibling may have ended before recording its own session.
                // Reconcile only terminal runs with owned process evidence, never TUIs.
                foreach (var peerId in store.GetSessionJobs(parent.JobId))
                {
                    if (store.GetJob(peerId) is not { } peer) { continue; } // Concurrent prune.
                    if (peer.Status == JobStatus.NeedsReconciliation
                        && reconcileIdleInteractive?.Invoke(peer) == true) { continue; }
                    var peerRuns = store.GetRuns(peerId);
                    if (peer.Status is JobStatus.NeedsReconciliation or JobStatus.Cancelled && peerRuns.Count > 0
                        && peerRuns[^1].BackendPid is not null && peerRuns.All(r => r.State != "started")
                        && !OrphanedBackendProcess.HasMarkedProcess([.. peerRuns.Select(r => r.Correlation)],
                            [.. peerRuns.Where(r => r.BackendPid.HasValue).Select(r => r.BackendPid!.Value)], readProcessEnvironment))
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
                            || OrphanedBackendProcess.HasMarkedProcess([.. runs.Select(r => r.Correlation)],
                                [.. runs.Where(r => r.BackendPid.HasValue).Select(r => r.BackendPid!.Value)], readProcessEnvironment))))
                {
                    return JobResult.Fail(JobErrors.ParentNotReady);
                }

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
        var options = "behavior=complete;hold=0" + (request.Interrupt ? ";interrupt=1" : "")
            + (defer ? ";defer=1" : "") + (nativeCodex ? ";native_codex=1" : "")
            + (nativeClaude ? ";native_claude=1" : "")
            + (!request.ReplaceIfIdle ? ";replace_if_idle=0" : "");
        if (selection.model is not null)
        {
            options += ";model=" + selection.model;
        }
        if (selection.effort is not null)
        {
            options += ";effort=" + selection.effort;
        }
        if (JobOptions.Read(parent.Options, "herdr_placement") is { } placement)
        {
            options += ";herdr_placement=" + placement;
        }
        return accept.Admit(Operation, request.IdempotencyKey, request.Instruction, options,
            parent.Backend, parent.Cwd, parent.JobId, request.WakeKey, request.WakeGeneration, worktreeBase: parent.WorktreeBase,
            worktreePath: parent.WorktreePath, worktreeBranch: parent.WorktreeBranch,
            timeoutSeconds: request.TimeoutSeconds, queueTtlSeconds: request.QueueTtlSeconds,
            interruptParent: interruptRunning, cancelRunning: cancelRunning, leadSessionId: request.LeadSessionId,
            targetAgent: parent.TargetAgent, deferParent: defer && !request.Interrupt);
    }
}
