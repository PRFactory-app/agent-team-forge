using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Business.Features.Jobs;

static class ReconciledSession
{
    /// <summary>
    /// Fences the session so no follow-up can be admitted, then checks no peer is queued or running.
    /// On refusal (or an error) only the job's own newly acquired fence is released. On success the caller cancels the job,
    /// which clears the fence, and releases it itself if that write fails and <paramref name="acquired"/> is true.
    /// </summary>
    public static bool TryClaimIdle(JobStore store, string jobId, out bool acquired, Func<string, bool>? inFlight = null)
    {
        bool fenced;
        (fenced, acquired) = store.TryFenceJobForStop(jobId);
        if (!fenced) { return false; }
        var ownFence = acquired;
        var idle = false;
        try
        {
            idle = !store.GetSessionJobs(jobId).Any(p => inFlight?.Invoke(p) == true
                || store.GetJob(p)?.Status is JobStatus.Queued or JobStatus.Running);
        }
        finally
        {
            if (!idle && ownFence) { store.ReconcileStoppedJob(jobId); }
        }
        return idle;
    }

}
