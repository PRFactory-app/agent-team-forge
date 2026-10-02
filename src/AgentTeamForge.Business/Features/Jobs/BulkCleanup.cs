using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Counts reported to the console; <c>Candidates</c> is what a dry run would act on.</summary>
public sealed record BulkCounts(int Stopped, int SkippedBusy, int SkippedUnverified, int Candidates, int Archived, int Remaining = 0)
{
    public IReadOnlyDictionary<string, int> ToMap() => new Dictionary<string, int>
    {
        ["stopped"] = Stopped,
        ["skipped_busy"] = SkippedBusy,
        ["skipped_unverified"] = SkippedUnverified,
        ["candidates"] = Candidates,
        ["archived"] = Archived,
        ["remaining"] = Remaining,
    };
}

/// <summary>
/// Console "Stop idle agents": closes the retained agent of every finished job whose pane a multi-sample probe proves idle.
/// Candidates are handled one at a time: probe, atomic fence (refused if any peer is unfinished), final re-probe of the
/// same pane, close. A busy, unverifiable or replaced pane is skipped and its fence released. The pass stops at
/// <c>budget</c> (below the console's IPC deadline) and reports how many candidates remain for another click.
/// </summary>
public sealed class StopIdleAgents(JobStore store, BoundPrincipal principal, BackendCatalog backends, StopAgent stopAgent, TimeSpan? budget = null)
{
    const int MaxJobsScanned = 5000;
    // The production pass runs in the background and is polled, so it has no request deadline; tests pass a small budget.
    readonly TimeSpan budget = budget ?? TimeSpan.FromDays(1);
    readonly Lock gate = new();
    Task? running;
    BulkCounts progress = new(0, 0, 0, 0, 0);

    /// <summary>Starts one background pass (the console polls <see cref="Status"/>); false when one is already running.</summary>
    public bool Start()
    {
        lock (gate)
        {
            if (running is { IsCompleted: false }) { return false; }
            progress = new BulkCounts(0, 0, 0, 0, 0);
            running = Task.Run(async () =>
            {
                try
                {
                    var final = await ExecuteAsync(false, counts => { lock (gate) { progress = counts; } });
                    lock (gate) { progress = final; }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* The next pass starts clean; fences are released per candidate. */ }
            });
            return true;
        }
    }

    public (bool Running, BulkCounts Counts) Status()
    {
        lock (gate) { return (running is { IsCompleted: false }, progress); }
    }

    static bool Finished(string? status) => status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;

    public async Task<BulkCounts> ExecuteAsync(bool dryRun, Action<BulkCounts>? report = null)
    {
        var seen = new HashSet<string>();
        var candidates = new List<(JobRecord Job, string SessionId, IInteractiveSessionStop Backend)>();
        foreach (var job in store.ListJobs(principal.Principal, principal.Team, MaxJobsScanned))
        {
            if (job.SessionId is not { } sessionId || !Finished(job.Status)
                || backends.Resolve(job.Backend) is not IInteractiveSessionStop backend || !seen.Add(job.Backend + "/" + sessionId)) { continue; }
            if (store.GetSessionJobs(job.JobId).Any(id => !Finished(store.GetJob(id)?.Status)) || backend.HasLiveSession(sessionId) != true) { continue; }
            candidates.Add((job, sessionId, backend));
        }
        if (dryRun) { return new BulkCounts(0, 0, 0, candidates.Count, 0); }

        report?.Invoke(new BulkCounts(0, 0, 0, candidates.Count, 0, candidates.Count));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int stopped = 0, busy = 0, unverified = 0, handled = 0;
        foreach (var (job, sessionId, backend) in candidates)
        {
            if (clock.Elapsed >= budget) { break; }
            handled++;
            var state = await Probe(backend, sessionId, budget - clock.Elapsed);
            if (state == SessionIdleState.Busy) { busy++; continue; }
            if (state != SessionIdleState.Idle) { unverified++; continue; }
            var identity = backend.LaunchIdentity(sessionId);
            var result = stopAgent.ExecuteBulk(job.JobId, () =>
            {
                // Under the fence, the same pane must still prove idle; a replaced pane or a slow probe skips it.
                var final = Probe(backend, sessionId, TimeSpan.FromSeconds(2) + (budget - clock.Elapsed > TimeSpan.Zero ? budget - clock.Elapsed : TimeSpan.Zero))
                    .GetAwaiter().GetResult();
                return final == SessionIdleState.Idle && Equals(identity, backend.LaunchIdentity(sessionId));
            });
            if (result.Error is null && result.Outcome == "agent_stopped") { stopped++; }
            else if (result.Error == JobErrors.ParentNotReady) { busy++; }
            else { unverified++; }
            report?.Invoke(new BulkCounts(stopped, busy, unverified, candidates.Count, 0, candidates.Count - handled));
        }
        return new BulkCounts(stopped, busy, unverified, candidates.Count, 0, candidates.Count - handled);
    }

    static async Task<SessionIdleState> Probe(IInteractiveSessionStop backend, string sessionId, TimeSpan limit)
    {
        try { return await backend.ProbeIdleAsync(sessionId).WaitAsync(limit > TimeSpan.Zero ? limit : TimeSpan.FromMilliseconds(1)); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException) { return SessionIdleState.Unverified; }
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
