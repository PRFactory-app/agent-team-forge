using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Commits cancellation before asking the dispatcher to stop its owned backend run.</summary>
public sealed class StopJob(JobStore store, BoundPrincipal principal, Action<string> cancelRunning, Action<string>? closeUnclaimedFollowUp = null)
{
    public JobResult Execute(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 64)
        {
            return JobResult.Fail(JobErrors.InvalidRequest);
        }

        try
        {
            var outcome = store.Cancel(jobId, principal.Principal, principal.Team);
            if (outcome.Job is null)
            {
                return JobResult.Fail(JobErrors.NotFound);
            }

            if (outcome.WasRunning)
            {
                cancelRunning(jobId);
            }
            else if (outcome.Changed)
            {
                closeUnclaimedFollowUp?.Invoke(jobId);
            }

            return JobResult.Ok(GetJob.ToView(outcome.Job), outcome.Changed ? "stopped" : "unchanged");
        }
        catch (StorageException ex)
        {
            return JobResult.Fail(JobErrors.FromStorage(ex));
        }
    }
}
