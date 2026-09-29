using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Stops an owned interactive session after its turn, including quarantined restart ownership.</summary>
public sealed class StopAgent(JobStore store, BoundPrincipal principal, BackendCatalog backends,
    Func<JobRecord, bool>? hasIdleInteractive = null, Func<JobRecord, bool>? settleCompletedInteractive = null,
    Func<string, JobResult>? stopJob = null, TimeSpan? settleWait = null, Func<IDisposable>? pauseClaims = null)
{
    readonly TimeSpan settleWait = settleWait ?? TimeSpan.FromSeconds(5);

    public JobResult Execute(string jobId) => Execute(jobId, null);

    /// <param name="jobId">The job whose agent session to stop.</param>
    /// <param name="mayStop">Lead-access check for the session peer whose running turn would be cancelled.</param>
    public JobResult Execute(string jobId, Func<string, bool>? mayStop)
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
            // A lead often stops right after the agent's DONE message, before the run loop records the turn
            // (final message not yet written, or a background task still pending). A verified idle TUI is settled here.
            var active = job.Status == JobStatus.Running ? job
                : store.GetSessionJobs(job.JobId).Select(store.GetJob).FirstOrDefault(peer => peer?.Status == JobStatus.Running);
            if (active is not null && mayStop?.Invoke(active.JobId) == false)
            {
                return JobResult.Fail(JobErrors.InvalidRequest, $"Job {active.JobId} in this agent session belongs to another lead session; it was not touched.");
            }
            if (active is not null && (stopJob is null || hasIdleInteractive?.Invoke(active) != true))
            {
                return JobResult.Fail(JobErrors.InvalidRequest, (active.JobId == job.JobId ? $"Job {jobId} is running" : $"Job {active.JobId} in this agent session is running")
                    + " and its agent is still working (or its prompt is not yet acknowledged, or it is not an interactive agent). Use stop_job to cancel the turn and close the agent, "
                    + "or interrupt_job to stop the turn and keep the agent; stop_agent works once the turn has ended.");
            }
            var cancelledTurn = false;
            if (active is not null)
            {
                // The completion record can trail the idle TUI by seconds (final message flush, stop hooks):
                // give the normal settle path a bounded chance before cancelling anything.
                // Waiting holds no lock and mutates nothing; only the final re-check and cancel run under the claim gate.
                var deadline = DateTimeOffset.UtcNow + settleWait;
                bool finished;
                while (!(finished = store.GetJob(active.JobId)?.Status != JobStatus.Running
                           || settleCompletedInteractive?.Invoke(active) == true)
                       && DateTimeOffset.UtcNow < deadline)
                {
                    Thread.Sleep(250);
                }
                using var claimsPaused = pauseClaims?.Invoke();
                var current = store.GetJob(active.JobId);
                if (current?.Status == JobStatus.Running)
                {
                    if (finished)
                    {
                        return JobResult.Fail(JobErrors.ParentNotReady, $"Job {active.JobId} finished its turn and ATF is still recording it; retry stop_agent in a few seconds.");
                    }
                    if (hasIdleInteractive?.Invoke(current) != true)
                    {
                        // It resumed work during the wait (e.g. a background-task notification started a new step).
                        return JobResult.Fail(JobErrors.InvalidRequest, $"Job {active.JobId} resumed work; use stop_job or interrupt_job, or retry stop_agent after the turn ends.");
                    }
                    var stoppedTurn = stopJob!(active.JobId);
                    if (stoppedTurn.Error is not null) { return stoppedTurn; }
                    cancelledTurn = true;
                }
                job = store.GetJob(jobId)!;
            }
            // The whole session closes, so no deferred turn of any job on it may start afterwards.
            // Only cancelled once every refusal check has passed.
            void CancelDeferred() { foreach (var peer in store.GetSessionJobs(job.JobId).Append(job.JobId).Distinct()) { store.CancelDeferredChildren(peer); } }
            var backend = backends.Resolve(job.Backend);
            var stopped = false;
            if (backend is HerdrInteractiveBackend herdr)
            {
                lock (herdr.SessionStopGate)
                {
                    var peers = store.GetSessionJobs(job.JobId);
                    if (!herdr.HasOwnedJobs(peers))
                    {
                        // StopJob's terminate already closed the pane of a cancelled turn.
                        if (cancelledTurn) { CancelDeferred(); return JobResult.Ok(GetJob.ToView(job), "agent_stopped"); }
                        if (store.IsSessionFenced(job.JobId))
                        {
                            return JobResult.Fail(JobErrors.BackendUnavailable, "The session is fenced after a daemon restart and ATF no longer owns a Herdr pane for it; there is no agent to close.");
                        }
                        CancelDeferred();
                        return JobResult.Ok(GetJob.ToView(job), "agent_not_running");
                    }
                    if (!store.TryFenceSessionForStop(job.JobId))
                    {
                        return JobResult.Fail(JobErrors.ParentNotReady, "Another turn in this agent session started; stop that job (stop_job) or retry stop_agent after it ends.");
                    }
                    CancelDeferred();
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
            if (backend is not HerdrInteractiveBackend) { CancelDeferred(); }
            return JobResult.Ok(GetJob.ToView(store.GetJob(jobId)!), stopped || cancelledTurn ? "agent_stopped" : "agent_not_running");
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
