using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Counts reported to the console; <c>Candidates</c> is what a dry run would act on.</summary>
public sealed record BulkCounts(int Stopped, int SkippedBusy, int SkippedUnverified, int Candidates, int Archived)
{
    public IReadOnlyDictionary<string, int> ToMap() => new Dictionary<string, int>
    {
        ["stopped"] = Stopped,
        ["skipped_busy"] = SkippedBusy,
        ["skipped_unverified"] = SkippedUnverified,
        ["candidates"] = Candidates,
        ["archived"] = Archived,
    };
}

/// <summary>
/// Console "Stop idle agents": closes the retained agent of every finished job whose pane a multi-sample probe proves idle.
/// A session with any queued/running/needs_reconciliation job, a busy pane or an unprovable pane is never touched.
/// Each pane is closed through the ordinary <see cref="StopAgent"/> path.
/// </summary>
public sealed class StopIdleAgents(JobStore store, BoundPrincipal principal, BackendCatalog backends, StopAgent stopAgent)
{
    const int MaxJobsScanned = 5000;
    const int ProbeParallelism = 8;

    public async Task<BulkCounts> ExecuteAsync(bool dryRun)
    {
        var seen = new HashSet<string>();
        var candidates = new List<(JobRecord Job, string SessionId, IInteractiveSessionStop Backend)>();
        foreach (var job in store.ListJobs(principal.Principal, principal.Team, MaxJobsScanned))
        {
            if (job.SessionId is not { } sessionId || job.Status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                || backends.Resolve(job.Backend) is not IInteractiveSessionStop backend || !seen.Add(job.Backend + "/" + sessionId)) { continue; }
            var peers = store.GetSessionJobs(job.JobId);
            if (peers.Any(id => store.GetJob(id)?.Status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled))
                || backend.HasLiveSession(sessionId) != true) { continue; }
            candidates.Add((job, sessionId, backend));
        }
        if (dryRun) { return new BulkCounts(0, 0, 0, candidates.Count, 0); }

        using var gate = new SemaphoreSlim(ProbeParallelism);
        var probed = await Task.WhenAll(candidates.Select(async c =>
        {
            await gate.WaitAsync();
            try { return await c.Backend.ProbeIdleAsync(c.SessionId); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return SessionIdleState.Unverified; }
            finally { gate.Release(); }
        }));

        int stopped = 0, busy = 0, unverified = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (probed[i] == SessionIdleState.Busy) { busy++; continue; }
            if (probed[i] != SessionIdleState.Idle) { unverified++; continue; }
            // Re-check right before the close: a follow-up may have been accepted while other panes were probed.
            if (store.GetSessionJobs(candidates[i].Job.JobId).Any(id => store.GetJob(id)?.Status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)))
            {
                busy++;
                continue;
            }
            var result = stopAgent.Execute(candidates[i].Job.JobId);
            if (result.Error is null && result.Outcome == "agent_stopped") { stopped++; }
            else if (result.Error == JobErrors.ParentNotReady) { busy++; }
            else { unverified++; }
        }
        return new BulkCounts(stopped, busy, unverified, candidates.Count, 0);
    }
}

/// <summary>
/// Console "Clear finished": durably archives (never deletes) finished follow-up chains.
/// Rule: a chain is archived only when every job in it is completed, failed or cancelled; any new follow-up
/// un-archives its whole chain. MCP/CLI job reads ignore the mark.
/// </summary>
public sealed class ArchiveJobs(JobStore store, BoundPrincipal principal)
{
    public int Execute(bool dryRun) => store.ArchiveFinished(principal.Principal, principal.Team, dryRun);
}
