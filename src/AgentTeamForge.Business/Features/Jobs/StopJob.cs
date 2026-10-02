using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>What a reconciled stop observed: nothing stopped, an owned agent closed, or no ownership record left.</summary>
public enum ReconcileStop { Refused, Stopped, NoRecord }

/// <summary>Commits cancellation before asking the dispatcher to stop its owned backend run.</summary>
public sealed class StopJob(JobStore store, BoundPrincipal principal, Action<string> cancelRunning, Action<string>? closeUnclaimedFollowUp = null, Func<JobRecord, ReconcileStop>? stopReconciled = null, Action<JobRecord>? forgetReconciledOwnership = null, Action<string>? interruptRunning = null, Func<JobRecord, bool>? releaseNative = null)
{
    public const string AbsentOutcome = "stopped_absent";

    public JobResult Execute(string jobId) => Execute(jobId, false);

    public JobResult Execute(string jobId, bool interrupt)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 64)
        {
            return JobResult.Fail(JobErrors.InvalidRequest, "job_id is required (at most 64 characters).");
        }

        try
        {
            var current = store.GetJob(jobId);
            if (current is null || current.Principal != principal.Principal || current.Team != principal.Team)
            {
                return JobResult.Fail(JobErrors.NotFound, $"No job {jobId} for this principal/team.");
            }
            // A native Codex or Claude mailbox turn owns no process here; stopping releases its N5 fence.
            if (!interrupt && releaseNative?.Invoke(current) == true)
            {
                return JobResult.Ok(GetJob.ToView(store.GetJob(jobId)!), "native_released");
            }
            if (current.Status == JobStatus.NeedsReconciliation)
            {
                var observed = stopReconciled?.Invoke(current) ?? ReconcileStop.Refused;
                if (observed == ReconcileStop.Refused)
                {
                    return JobResult.Fail(JobErrors.OwnershipNotProven,
                        "Job needs reconciliation and ATF could not prove it still owns the agent (no owned Herdr pane / marked process); nothing was stopped. Inspect get_job and the pane.");
                }
                // With no ownership record left, or a fence cleared since the owned stop, peers must be checked atomically with the cancel.
                var stopped = store.CancelReconciled(jobId, principal.Principal, principal.Team,
                    requireIdlePeers: observed == ReconcileStop.NoRecord, requireIdlePeersIfUnfenced: true);
                if (stopped.PeerActive)
                {
                    return JobResult.Fail(JobErrors.OwnershipNotProven,
                        "Another turn on this agent session was admitted while it was being stopped; nothing was cancelled or forgotten. Stop that job (stop_job) or retry after it ends.");
                }
                if (stopped.Changed) { forgetReconciledOwnership?.Invoke(current); }
                // "stopped_absent": the stop only cancelled the row because no owned pane or marked process remained (NoRecord).
                return JobResult.Ok(GetJob.ToView(stopped.Job!), !stopped.Changed ? "unchanged" : observed == ReconcileStop.NoRecord ? AbsentOutcome : "stopped");
            }
            if (interrupt && interruptRunning is null) { return JobResult.Fail(JobErrors.BackendUnavailable, "interrupt_job is not supported for this job; use stop_job."); }
            var outcome = store.Cancel(jobId, principal.Principal, principal.Team, interrupt);
            if (outcome.Job is null)
            {
                return JobResult.Fail(JobErrors.NotFound, $"No job {jobId} for this principal/team.");
            }

            if (outcome.WasRunning)
            {
                if (interrupt) { interruptRunning!(jobId); } else { cancelRunning(jobId); }
            }
            else if (outcome.Changed)
            {
                closeUnclaimedFollowUp?.Invoke(jobId);
            }

            return JobResult.Ok(GetJob.ToView(outcome.Job), outcome.Changed ? "stopped" : "unchanged");
        }
        catch (Exception ex) when (ex is HerdrLaunchException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return JobResult.Fail(JobErrors.OwnershipNotProven, $"Could not stop the agent: {ex.Message}");
        }
        catch (StorageException ex)
        {
            return JobResult.Fail(JobErrors.FromStorage(ex), JobErrors.StorageDetail(ex));
        }
    }
}
