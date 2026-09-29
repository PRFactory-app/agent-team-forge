using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Stops an owned interactive session after its turn, including quarantined restart ownership.</summary>
public sealed class StopAgent(JobStore store, BoundPrincipal principal, BackendCatalog backends,
    Func<JobRecord, bool>? settleCompletedInteractive = null, TimeSpan? settleWait = null)
{
    readonly TimeSpan settleWait = settleWait ?? TimeSpan.FromSeconds(5);

    public JobResult Execute(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 64)
        {
            return JobResult.Fail(JobErrors.InvalidRequest, "job_id is required (at most 64 characters).");
        }

        try
        {
            var job = store.GetJob(jobId);
            if (job is null || job.Principal != principal.Principal || job.Team != principal.Team)
            {
                return JobResult.Fail(JobErrors.NotFound, $"No job {jobId} for this principal/team.");
            }
            if (job.Status == JobStatus.Queued)
            {
                return JobResult.Fail(JobErrors.InvalidRequest, $"Job {jobId} is queued and has no agent yet; use stop_job to cancel it.");
            }
            if (job.Status == JobStatus.Running)
            {
                // A lead often stops right after the agent's DONE message, before the run loop records the turn.
                // Wait (holding nothing) for the normal settle path to complete the job; never cancel a running turn here.
                var deadline = DateTimeOffset.UtcNow + settleWait;
                while (store.GetJob(jobId)?.Status == JobStatus.Running
                       && settleCompletedInteractive?.Invoke(job) != true && DateTimeOffset.UtcNow < deadline)
                {
                    Thread.Sleep(250);
                }
                job = store.GetJob(jobId)!;
                if (job.Status == JobStatus.Running)
                {
                    return JobResult.Fail(JobErrors.InvalidRequest, $"Job {jobId} is still running (its agent turn has not finished or the result is not recorded yet); "
                        + "retry stop_agent in a few seconds, or use stop_job to cancel the turn and close the agent.");
                }
            }
            // The whole session closes, so no deferred turn of any job on it may start afterwards.
            foreach (var peer in store.GetSessionJobs(job.JobId).Append(job.JobId).Distinct()) { store.CancelDeferredChildren(peer); }
            var backend = backends.Resolve(job.Backend);
            var stopped = false;
            if (backend is HerdrInteractiveBackend herdr)
            {
                lock (herdr.SessionStopGate)
                {
                    var peers = store.GetSessionJobs(job.JobId);
                    if (!herdr.HasOwnedJobs(peers))
                    {
                        return store.IsSessionFenced(job.JobId)
                            ? JobResult.Fail(JobErrors.BackendUnavailable, "The session is fenced after a daemon restart and ATF no longer owns a Herdr pane for it; there is no agent to close.")
                            : JobResult.Ok(GetJob.ToView(job), "agent_not_running");
                    }
                    if (!store.TryFenceSessionForStop(job.JobId))
                    {
                        return JobResult.Fail(JobErrors.ParentNotReady, "Another turn in this agent session started; stop that job (stop_job) or retry stop_agent after it ends.");
                    }
                    stopped = herdr.StopOwnedJobs(peers);
                    if (stopped)
                    {
                        foreach (var peerId in peers)
                        {
                            if (store.GetJob(peerId)?.Status == JobStatus.NeedsReconciliation)
                            {
                                store.CancelReconciled(peerId, principal.Principal, principal.Team);
                            }
                        }
                        store.ReconcileStoppedSession(job.JobId);
                        herdr.ForgetStoppedJobs(peers);
                    }
                }
            }
            else
            {
                stopped = job.SessionId is { } sessionId
                    && backend is IInteractiveSessionStop interactive && interactive.StopIdleSession(sessionId);
                if (job.Status == JobStatus.NeedsReconciliation)
                {
                    var runs = store.GetRuns(job.JobId);
                    var markers = runs.Select(run => run.Correlation).ToArray();
                    OrphanedBackendProcess.TerminateMarked(markers);
                    if (OrphanedBackendProcess.HasMarkedProcess(markers,
                        [.. runs.Where(run => run.BackendPid.HasValue).Select(run => run.BackendPid!.Value)]))
                    {
                        return JobResult.Fail(JobErrors.OwnershipNotProven, "A marked backend process survived termination; the job was left fenced.");
                    }
                    store.CancelReconciled(job.JobId, principal.Principal, principal.Team);
                    stopped = true;
                }
            }
            return JobResult.Ok(GetJob.ToView(store.GetJob(jobId)!), stopped ? "agent_stopped" : "agent_not_running");
        }
        catch (StorageException ex)
        {
            return JobResult.Fail(JobErrors.FromStorage(ex), JobErrors.StorageDetail(ex));
        }
        catch (Exception ex) when (ex is HerdrLaunchException or IOException or UnauthorizedAccessException)
        {
            return JobResult.Fail(JobErrors.BackendUnavailable, $"Herdr control failed: {ex.Message}");
        }
    }
}
