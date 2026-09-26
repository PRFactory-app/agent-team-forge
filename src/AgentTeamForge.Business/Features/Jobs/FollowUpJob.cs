using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;

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

        // Resuming a session that is still in a turn would race the running agent.
        // A stopped parent's process tree was killed when its cancellation committed.
        if (parent.SessionId is null || parent.Status is not (JobStatus.Completed or JobStatus.Cancelled)
            && !(request.Interrupt && parent.Status == JobStatus.Running))
        {
            return JobResult.Fail(JobErrors.ParentNotReady);
        }

        if (request.Interrupt && parent.Status == JobStatus.Running && cancelRunning is null)
        {
            return JobResult.Fail(JobErrors.DaemonUnhealthy);
        }

        return accept.Admit(Operation, request.IdempotencyKey, request.Instruction,
            "behavior=complete;hold=0" + (request.Interrupt ? ";interrupt=1" : ""),
            parent.Backend, parent.Cwd, parent.JobId, request.WakeKey, request.WakeGeneration, worktreeBase: parent.WorktreeBase,
            worktreePath: parent.WorktreePath, worktreeBranch: parent.WorktreeBranch,
            timeoutSeconds: request.TimeoutSeconds, queueTtlSeconds: request.QueueTtlSeconds,
            interruptParent: request.Interrupt, cancelRunning: cancelRunning);
    }
}
