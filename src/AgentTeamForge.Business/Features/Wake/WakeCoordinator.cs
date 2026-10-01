using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Wake;

public interface IWakePoster
{
    Task<bool> PostAsync(WakeRegistration target, string notice, CancellationToken cancellationToken);
}

public sealed class WakeBackoff
{
    public TimeSpan Delay { get; private set; }
    public DateTimeOffset Until { get; private set; }
    public void Failed(DateTimeOffset now)
    {
        Delay = TimeSpan.FromSeconds(Math.Min(300, Delay == TimeSpan.Zero ? 2 : Delay.TotalSeconds * 2));
        Until = now + Delay;
    }
    public void Reset() { Delay = TimeSpan.Zero; Until = default; }
}

/// <summary>Polls committed terminal rows and unacknowledged interactive parks.</summary>
public sealed class WakeCoordinator(WakeStore store, IWakePoster poster, Action<string> log,
    Func<DateTimeOffset>? clock = null, TimeSpan? coalesce = null, TimeSpan? renotify = null)
{
    sealed class State(long generation)
    {
        public long Generation = generation;
        public DateTimeOffset? FirstNew;
        public WakeBackoff Backoff = new();
        public int Renotifies;
        public long LatestSeq;
    }
    static readonly TimeSpan MaxRenotify = TimeSpan.FromMinutes(60);
    readonly Dictionary<string, State> states = [];
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly TimeSpan coalesceWindow = coalesce ?? TimeSpan.FromSeconds(2);
    readonly TimeSpan renotifyWindow = renotify ?? TimeSpan.FromMinutes(5);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await TickAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log($"wake scan failed: {StorageException.Describe(ex)}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        var pending = store.Pending().Concat(store.PendingExternal()).Concat(store.PendingParks());
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in pending)
        {
            var target = snapshot.Target;
            var stateKey = target.Key + (snapshot.ParkJobId is not null ? ":park:" + snapshot.ParkJobId : snapshot.External ? ":external" : ":jobs");
            active.Add(stateKey);
            if (!states.TryGetValue(stateKey, out var state) || state.Generation != target.Generation)
            {
                state = new State(target.Generation);
                states[stateKey] = state;
            }
            var current = now();
            if (snapshot.LatestSeq > state.LatestSeq)
            {
                // A newly finished job must not wait behind retry/reminder backoff earned by older ones.
                state.LatestSeq = snapshot.LatestSeq;
                state.Backoff.Reset();
                state.Renotifies = 0;
                state.FirstNew = current;
            }
            if (current < state.Backoff.Until)
            {
                continue;
            }

            // A newly finished job wakes after the coalesce window; an unchanged unread set is
            // reminded at doubling intervals (renotify, 2x, 4x ... capped at an hour).
            var isNew = snapshot.LatestSeq > snapshot.NotifiedSeq;
            var interval = TimeSpan.FromTicks(Math.Min(MaxRenotify.Ticks, renotifyWindow.Ticks << Math.Min(state.Renotifies, 8)));
            var expired = snapshot.ParkJobId is null && !isNew && snapshot.LastSuccess is not null && current - snapshot.LastSuccess >= interval;
            if (!isNew && !expired)
            {
                continue;
            }

            state.FirstNew ??= current;
            if (current - state.FirstNew < coalesceWindow)
            {
                continue;
            }

            using var routing = await WakeRoutingGate.EnterAsync(cancellationToken);
            if (!store.IsCurrent(target)) { continue; }
            // Notice-only: job IDs and result content stay in get_job.
            var notice = snapshot.ParkJobId is not null
                ? "[AgentTeamForge wake] An interactive agent is idle without a native completion. Call mcp__agentteamforge__list_jobs and mcp__agentteamforge__get_job."
                : snapshot.External
                ? $"[AgentTeamForge wake] {snapshot.Unread} external message(s) await reading. Call mcp__agentteamforge__external_read or mcp__agentteamforge__read_messages."
                : $"[AgentTeamForge wake] {snapshot.Unread} job(s) finished or need attention. Call mcp__agentteamforge__list_jobs with unread=true, then get_job.";
            bool posted;
            try { posted = await poster.PostAsync(target, notice, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log($"wake post failed: {ex.GetType().Name}");
                posted = false;
            }
            if (posted && store.MarkNotified(snapshot, now()))
            {
                state.Backoff.Reset();
                state.FirstNew = null;
                state.Renotifies = isNew ? 0 : state.Renotifies + 1;
            }
            else
            {
                if (!posted)
                {
                    log($"wake post rejected: kind={target.Kind} target={target.Key} source={(snapshot.ParkJobId is not null ? "park" : snapshot.External ? "external" : "jobs")}");
                }
                state.Backoff.Failed(now());
            }
        }
        foreach (var key in states.Keys.Except(active).ToArray())
        {
            states.Remove(key);
        }
    }
}
