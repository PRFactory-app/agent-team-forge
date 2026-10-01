using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Authorized read of a job's committed state; never triggers resend.</summary>
public sealed class GetJob(JobStore store, BoundPrincipal principal, bool interactiveLaunch = false)
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
            // Another principal's job is indistinguishable from an unknown ID.
            return job is null || job.Principal != principal.Principal || job.Team != principal.Team
                ? JobResult.Fail(JobErrors.NotFound)
                : JobResult.Ok(View(job), "found");
        }
        catch (StorageException ex)
        {
            return JobResult.Fail(JobErrors.FromStorage(ex));
        }
    }

    /// <summary>Most recent jobs of the bound principal, without result text (keeps frames small).</summary>
    public JobResult List(int limit = 50)
    {
        try
        {
            var jobs = store.ListJobs(principal.Principal, principal.Team, Math.Clamp(limit, 1, 200));
            return new JobResult(null, "listed", null) { Jobs = [.. jobs.Select(j => View(j) with { Result = null, Instruction = null })] };
        }
        catch (StorageException ex)
        {
            return JobResult.Fail(JobErrors.FromStorage(ex));
        }
    }

    JobView View(JobRecord job) => ToView(job) with
    {
        Instruction = job.Instruction,
        ReasonCode = job.Status == JobStatus.Queued && job.ParentJobId is { } parent && store.IsSessionFenced(parent)
            ? "parent_needs_reconciliation" : job.ReasonCode,
        Startup = StartupProgress.Read(store, job.JobId, job.Status, job.Backend, job.ReasonCode, interactiveLaunch),
        Delivery = Delivery(job)
    };

    JobDelivery Delivery(JobRecord job)
    {
        var runs = store.GetRuns(job.JobId);
        var run = runs.Count == 0 ? null : runs[^1];
        var state = run?.Acked == true ? "acknowledged"
            : job.Status == JobStatus.Completed ? "result_observed"
            : run is not null ? "unconfirmed"
            : job.Status == JobStatus.Queued ? "pending" : "not_started";
        return new JobDelivery(state, run?.RunId, run?.SubmittedAt, run?.AcknowledgedAt)
        {
            NativeSubmissionId = store.NativeSubmissionId(job.JobId)
        };
    }

    internal static JobView ToView(JobRecord job) => new(job.JobId, job.Status, job.ResultText, job.ReasonCode, job.Attempts)
    {
        ExpectedOutputs = JobOptions.Read(job.Options, "expected_outputs")?.Split(',').Select(p => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(p))).ToArray(),
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
