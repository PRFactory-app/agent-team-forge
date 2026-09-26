using AgentTeamForge.DAL.Features.Wake;

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

/// <summary>Polls only committed terminal rows. Wake success does not consume them.</summary>
public sealed class WakeCoordinator(WakeStore store, IWakePoster poster, Action<string> log,
    Func<DateTimeOffset>? clock = null, TimeSpan? coalesce = null, TimeSpan? renotify = null)
{
    sealed class State(long generation)
    {
        public long Generation = generation;
        public DateTimeOffset? FirstNew;
        public WakeBackoff Backoff = new();
    }
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
                log($"wake scan failed: {ex.GetType().Name}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        var pending = store.Pending();
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in pending)
        {
            var target = snapshot.Target;
            active.Add(target.Key);
            if (!states.TryGetValue(target.Key, out var state) || state.Generation != target.Generation)
            {
                state = new State(target.Generation);
                states[target.Key] = state;
            }
            var current = now();
            if (current < state.Backoff.Until)
            {
                continue;
            }

            var expired = snapshot.LastSuccess is not null && current - snapshot.LastSuccess >= renotifyWindow;
            if (snapshot.Outstanding && !expired)
            {
                continue;
            }

            if (snapshot.LatestSeq <= snapshot.NotifiedSeq && !expired)
            {
                continue;
            }

            state.FirstNew ??= current;
            if (current - state.FirstNew < coalesceWindow)
            {
                continue;
            }

            if (!store.IsCurrent(target))
            {
                continue;
            }
            // Notice-only: job IDs and result content stay in get_job.
            var notice = $"[AgentTeamForge wake] {snapshot.Unread} completed job(s) await reading. Call list_jobs and get_job.";
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
            }
            else
            {
                state.Backoff.Failed(now());
            }
        }
        foreach (var key in states.Keys.Except(active).ToArray())
        {
            states.Remove(key);
        }
    }
}
