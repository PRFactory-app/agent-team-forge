using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>
/// Accepts a new job that resumes the parent's recorded native session on the
/// parent's backend and cwd. The parent must be finished and have a session.
/// </summary>
public sealed class FollowUpJob(JobStore store, BoundPrincipal principal, AcceptJob accept)
{
    public const string Operation = "job_follow_up";

    public JobResult Execute(FollowUpRequest request)
    {
        if (!accept.IsValid(request.IdempotencyKey, request.Instruction)
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
        if (parent.SessionId is null || parent.Status != JobStatus.Completed)
        {
            return JobResult.Fail(JobErrors.ParentNotReady);
        }

        return accept.Admit(Operation, request.IdempotencyKey, request.Instruction, "behavior=complete;hold=0",
            parent.Backend, parent.Cwd, parent.JobId, request.WakeKey, request.WakeGeneration);
    }
}
