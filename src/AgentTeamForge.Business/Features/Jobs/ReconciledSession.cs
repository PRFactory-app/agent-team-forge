using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Business.Features.Jobs;

static class ReconciledSession
{
    /// <summary>
    /// Fences the session so no follow-up can be admitted, then checks no peer is queued or running.
    /// On refusal the fence is released unless it was already set. On success the caller cancels the job,
    /// which clears the fence.
    /// </summary>
    public static bool TryClaimIdle(JobStore store, string jobId, Func<string, bool>? inFlight = null)
    {
        var wasFenced = store.IsSessionFenced(jobId);
        if (!store.TryFenceSessionForStop(jobId)) { return false; }
        if (store.GetSessionJobs(jobId).Any(p => inFlight?.Invoke(p) == true
            || store.GetJob(p)?.Status is JobStatus.Queued or JobStatus.Running))
        {
            if (!wasFenced) { store.ReconcileStoppedJob(jobId); }
            return false;
        }
        return true;
    }
}
