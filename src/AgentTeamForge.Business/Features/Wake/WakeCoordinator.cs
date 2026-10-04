using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Wake;

public interface IWakePoster
{
    Task<bool> PostAsync(WakeRegistration target, string notice, CancellationToken cancellationToken);

    /// <summary>A <see cref="WakePost"/> value: ok, or why the post was rejected.</summary>
    async Task<string> PostWithReasonAsync(WakeRegistration target, string notice, CancellationToken cancellationToken) =>
        await PostAsync(target, notice, cancellationToken) ? WakePost.Ok : WakePost.Rejected;
}

public static class WakePost
{
    public const string Ok = "ok";
    /// <summary>No bridge relay polled the channel recently: the Claude process or its MCP bridge is gone.</summary>
    public const string NoRelay = "no_relay";
    public const string Timeout = "timeout";
    public const string RelayFailed = "relay_failed";
    public const string Rejected = "rejected";
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
    Func<DateTimeOffset>? clock = null, TimeSpan? coalesce = null, TimeSpan? renotify = null,
    Func<WakeRegistration, bool>? claudeGone = null)
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
    readonly Func<WakeRegistration, bool> gone = claudeGone ?? (target => ClaudeTargetGone(target));
    readonly Dictionary<string, string> rejections = [];
    DateTimeOffset nextPrune;

    /// <summary>Unix (Linux/macOS): the channel socket file is missing and the owning pid, if recorded, has exited.
    /// Windows: the owning pid has exited; the named pipe dies with it and posts require that exact server pid.
    /// Any doubt (access denied, unknown platform, Windows without a pid) counts as alive.</summary>
    public static bool ClaudeTargetGone(WakeRegistration target, string? platform = null)
    {
        var transport = ClaudeChannel.Transport(platform ?? ClaudeChannel.Platform);
        if (transport is null || transport == "unix" && File.Exists(target.Address)) { return false; }
        if (!int.TryParse(target.Home, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            return transport == "unix";
        }
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

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
        if (now() >= nextPrune)
        {
            nextPrune = now() + TimeSpan.FromMinutes(10);
            foreach (var key in store.PruneDead(now(), gone))
            {
                log($"wake target pruned: target={key}");
            }
        }
        var pending = store.Pending().Concat(store.PendingExternal()).Concat(store.PendingParks());
        var active = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in pending)
        {
            var target = snapshot.Target;
            var stateKey = target.Key + (snapshot.ParkJobId is not null ? ":park:" + snapshot.ParkJobId : snapshot.External ? ":external" : ":jobs");
            active.Add(stateKey);
            targets.Add(target.Key);
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
            // Notice-only: job IDs and result content stay in get_job. The external notice carries the inbox
            // revision because Claude Code silently drops a channel message repeating the previous body within 30s.
            var notice = snapshot.ParkJobId is not null
                ? "[AgentTeamForge wake] An interactive agent is idle without a native completion. Call mcp__agentteamforge__list_jobs and mcp__agentteamforge__get_job."
                : snapshot.External
                ? $"[AgentTeamForge wake] {snapshot.Unread} external message(s) await reading (inbox revision {snapshot.LatestSeq}). Call mcp__agentteamforge__external_read or mcp__agentteamforge__read_messages."
                : $"[AgentTeamForge wake] {snapshot.Unread} job(s) finished or need attention. Call mcp__agentteamforge__list_jobs with unread=true, then get_job.";
            string reason;
            try { reason = await poster.PostWithReasonAsync(target, notice, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                reason = "exception:" + ex.GetType().Name;
            }
            var posted = reason == WakePost.Ok;
            if (posted) { rejections.Remove(target.Key); }
            if (posted && store.MarkNotified(snapshot, now()))
            {
                state.Backoff.Reset();
                state.FirstNew = null;
                state.Renotifies = isNew ? 0 : state.Renotifies + 1;
            }
            else
            {
                // Logged once per target and reason; the backoff keeps retrying quietly.
                if (!posted && (!rejections.TryGetValue(target.Key, out var last) || last != reason))
                {
                    rejections[target.Key] = reason;
                    log($"wake post rejected: kind={target.Kind} target={target.Key} source={(snapshot.ParkJobId is not null ? "park" : snapshot.External ? "external" : "jobs")} reason={reason}");
                }
                state.Backoff.Failed(now());
            }
        }
        foreach (var key in states.Keys.Except(active).ToArray())
        {
            states.Remove(key);
        }
        foreach (var key in rejections.Keys.Except(targets).ToArray())
        {
            rejections.Remove(key);
        }
    }
}
