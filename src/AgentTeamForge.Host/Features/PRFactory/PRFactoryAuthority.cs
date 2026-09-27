using System.Net;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Host.Features.PRFactory;

/// <summary>
/// One instance per connector. All turn creation (including generic follow-up), dispatch and
/// publication must use RunAsync. The gate orders admission against durable fencing; already
/// started remote operations cannot be undone. Persist their receipts and recheck before completion.
/// </summary>
public sealed class PRFactoryAuthority(
    string server, PRFactoryAuthorityStore store, PRFactoryTeamStore teams,
    Func<string, JobResult> stopJob, Func<string, JobResult> stopAgent,
    Func<string, string, bool> revokeMember, Func<string, bool> executionStopped) : IDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);
    readonly HashSet<Guid> confirmed = [];
    readonly Dictionary<Guid, int> inFlight = [];
    public bool IntakeBlocked { get; private set; }
    public string Server => server;

    public void Dispose() => gate.Dispose();

    /// <summary>Dispatcher admission for a queued owned turn: only while this instance holds fresh confirmation.</summary>
    public bool MayLaunch(Guid id)
    {
        lock (confirmed) { return confirmed.Contains(id); }
    }

    public async Task SuspendAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { lock (confirmed) { confirmed.Clear(); } }
        finally { gate.Release(); }
        await RetryStopsAsync(ct);
    }

    public async Task<bool> RunAsync(Guid id, Func<Task> effect, CancellationToken ct = default)
    {
        Task pending;
        await gate.WaitAsync(ct);
        try
        {
            if (IntakeBlocked || !MayLaunch(id) || store.Read(server).SingleOrDefault(r => r.WorkItemId == id)?.Disposition != "accepted")
            {
                return false;
            }
            pending = effect();
            inFlight[id] = inFlight.GetValueOrDefault(id) + 1;
        }
        finally { gate.Release(); }
        try { await pending; }
        finally
        {
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                if (--inFlight[id] == 0) { inFlight.Remove(id); }
            }
            finally { gate.Release(); }
            // A submit may have persisted its member mapping after the initial stop scan.
            await RetryStopsAsync(CancellationToken.None);
        }
        return true;
    }

    public async Task ObserveAsync(Guid id, string disposition, string? reason = null, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            store.Set(server, id, disposition, reason);
            lock (confirmed)
            {
                if (disposition == "accepted" && store.Read(server).Single(r => r.WorkItemId == id).Disposition == "accepted")
                {
                    confirmed.Add(id);
                }
                else { confirmed.Remove(id); }
            }
        }
        finally { gate.Release(); }
        await RetryStopsAsync(ct);
    }

    public async Task TransportFailureAsync(Guid id, HttpStatusCode? status, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            lock (confirmed) { confirmed.Remove(id); } // Outage preserves ownership but requires fresh confirmation for mutations.
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                IntakeBlocked = true;
                lock (confirmed) { confirmed.Clear(); }
                var owned = teams.Pending(server).Select(t => t.WorkItemId)
                    .Concat(store.Read(server).Where(r => r.Disposition == "accepted").Select(r => r.WorkItemId)).Distinct();
                foreach (var item in owned)
                {
                    store.Set(server, item, "reconciliation-needed", "token_rejected");
                }
            }
            else if (status == HttpStatusCode.NotFound)
            {
                store.Set(server, id, "reconciliation-needed", "accepted_item_missing");
            }
        }
        finally { gate.Release(); }
        await RetryStopsAsync(ct);
    }

    public async Task RetryStopsAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            foreach (var row in store.Read(server).Where(r => r.Stopping))
            {
                var complete = !inFlight.ContainsKey(row.WorkItemId);
                // Enumerate descendants too: deferred deliveries may not yet have a member mapping.
                foreach (var job in store.OwnedTurns(server, row.WorkItemId))
                {
                    try
                    {
                        var cancelled = stopJob(job);
                        var closed = stopAgent(job); // Includes completed/retained interactive sessions.
                        complete &= cancelled.Error is null && closed.Error is null && executionStopped(job);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                    {
                        complete = false;
                    }
                }
                foreach (var member in teams.ExternalMembers(server, row.WorkItemId).Where(m => !m.Closed))
                {
                    // Existing revocation invalidates tickets/mailbox access; never touches a human PID.
                    if (revokeMember(member.TeamId, member.ActualName))
                    {
                        teams.MarkExternalClosed(server, row.WorkItemId, member.Member);
                    }
                    else { complete = false; }
                }
                if (complete) { store.Stopped(server, row.WorkItemId); }
            }
        }
        finally { gate.Release(); }
    }
}
