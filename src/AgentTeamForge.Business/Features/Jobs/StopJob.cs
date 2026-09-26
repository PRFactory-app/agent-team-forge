using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Commits cancellation before asking the dispatcher to stop its owned backend run.</summary>
public sealed class StopJob(JobStore store, BoundPrincipal principal, Action<string> cancelRunning, Action<string>? closeUnclaimedFollowUp = null, Func<JobRecord, bool>? stopReconciled = null, Action<JobRecord>? forgetReconciledOwnership = null)
{
    public JobResult Execute(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 64)
        {
            return JobResult.Fail(JobErrors.InvalidRequest);
        }

        try
        {
            var current = store.GetJob(jobId);
            if (current is null || current.Principal != principal.Principal || current.Team != principal.Team)
            {
                return JobResult.Fail(JobErrors.NotFound);
            }
            if (current.Status == JobStatus.NeedsReconciliation)
            {
                if (stopReconciled is null || !stopReconciled(current))
                {
                    return JobResult.Fail(JobErrors.OwnershipNotProven);
                }
                var stopped = store.CancelReconciled(jobId, principal.Principal, principal.Team);
                if (stopped.Changed) { forgetReconciledOwnership?.Invoke(current); }
                return JobResult.Ok(GetJob.ToView(stopped.Job!), stopped.Changed ? "stopped" : "unchanged");
            }
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
        catch (Exception ex) when (ex is HerdrLaunchException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return JobResult.Fail(JobErrors.OwnershipNotProven);
        }
        catch (StorageException ex)
        {
            return JobResult.Fail(JobErrors.FromStorage(ex));
        }
    }
}
