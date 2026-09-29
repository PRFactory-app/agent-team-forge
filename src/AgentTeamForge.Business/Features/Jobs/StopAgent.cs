using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Stops an owned interactive session after its turn, including quarantined restart ownership.</summary>
public sealed class StopAgent(JobStore store, BoundPrincipal principal, BackendCatalog backends)
{
    public JobResult Execute(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 64)
        {
            return JobResult.Fail(JobErrors.InvalidRequest);
        }

        try
        {
            var job = store.GetJob(jobId);
            if (job is null || job.Principal != principal.Principal || job.Team != principal.Team)
            {
                return JobResult.Fail(JobErrors.NotFound);
            }
            if (job.Status is JobStatus.Queued or JobStatus.Running)
            {
                return JobResult.Fail(JobErrors.InvalidRequest);
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
                            ? JobResult.Fail(JobErrors.BackendUnavailable)
                            : JobResult.Ok(GetJob.ToView(job), "agent_not_running");
                    }
                    if (!store.TryFenceSessionForStop(job.JobId))
                    {
                        return JobResult.Fail(JobErrors.ParentNotReady);
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
                        return JobResult.Fail(JobErrors.OwnershipNotProven);
                    }
                    store.CancelReconciled(job.JobId, principal.Principal, principal.Team);
                    stopped = true;
                }
            }
            return JobResult.Ok(GetJob.ToView(store.GetJob(jobId)!), stopped ? "agent_stopped" : "agent_not_running");
        }
        catch (StorageException ex)
        {
            return JobResult.Fail(JobErrors.FromStorage(ex));
        }
        catch (Exception ex) when (ex is HerdrLaunchException or IOException or UnauthorizedAccessException)
        {
            return JobResult.Fail(JobErrors.BackendUnavailable);
        }
    }
}
