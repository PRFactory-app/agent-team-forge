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
            return JobResult.Fail(JobErrors.FromStorage(ex));
        }

        // Another principal's job is indistinguishable from an unknown ID.
        return job is null || job.Principal != principal.Principal || job.Team != principal.Team
            ? JobResult.Fail(JobErrors.NotFound)
            : JobResult.Ok(ToView(job), "found");
    }

    /// <summary>Most recent jobs of the bound principal, without result text (keeps frames small).</summary>
    public JobResult List(int limit = 50)
    {
        try
        {
            var jobs = store.ListJobs(principal.Principal, principal.Team, Math.Clamp(limit, 1, 200));
            return new JobResult(null, "listed", null) { Jobs = [.. jobs.Select(j => ToView(j) with { Result = null })] };
        }
        catch (StorageException ex)
        {
            return JobResult.Fail(JobErrors.FromStorage(ex));
        }
    }

    internal static JobView ToView(JobRecord job) => new(job.JobId, job.Status, job.ResultText, job.ReasonCode, job.Attempts)
    {
        Backend = job.Backend,
        Model = JobOptions.Read(job.Options, "model"),
        Effort = JobOptions.Read(job.Options, "effort"),
        HerdrPlacement = JobOptions.Read(job.Options, "herdr_placement"),
        SessionId = job.SessionId,
        ParentJobId = job.ParentJobId,
        Cwd = job.WorktreePath ?? job.Cwd,
        WorktreePath = job.WorktreePath,
        WorktreeBranch = job.WorktreeBranch,
    };
}
