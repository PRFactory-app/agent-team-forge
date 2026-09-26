using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Authorized read of a job's committed state; never triggers resend.</summary>
public sealed class GetJob(JobStore store, BoundPrincipal principal)
{
    public JobResult Execute(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 64)
        {
            return JobResult.Fail(JobErrors.InvalidRequest);
        }

        JobRecord? job;
        try
        {
            job = store.GetJob(jobId);
        }
        catch (StorageException ex)
        {
            return JobResult.Fail(ex.Failure == StorageFailure.Busy ? JobErrors.StorageBusy : JobErrors.StorageUnavailable);
        }

        // Another principal's job is indistinguishable from an unknown ID.
        return job is null || job.Principal != principal.Principal || job.Team != principal.Team
            ? JobResult.Fail(JobErrors.NotFound)
            : JobResult.Ok(ToView(job), "found");
    }

    internal static JobView ToView(JobRecord job) => new(job.JobId, job.Status, job.ResultText, job.ReasonCode, job.Attempts);
}
