namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>
/// Idle interactive sessions this daemon keeps open for a follow-up or Stop agent.
/// Past the configured idle timeout or cap the tab is closed; failed cleanup remains retryable.
/// </summary>
internal sealed class RetainedSessions : IDisposable
{
    public const int MaxRetained = 16;
    // Busy probes refresh the idle clock but never this: a pane that never goes idle is still reclaimed.
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(24);
    readonly Lock _gate = new();
    readonly Dictionary<string, (InteractiveLaunch Launch, long Order, long IdleSince, long Born)> _sessions = [];
    readonly Dictionary<string, InteractiveLaunch?> _reserved = [];
    readonly Dictionary<string, (InteractiveLaunch Launch, bool Live)> _known = [];
    long _next;
    readonly Action<InteractiveLaunch> _stop;
    readonly TimeProvider _clock;
    readonly TimeSpan _timeout;
    readonly ITimer _timer;
    readonly Func<InteractiveRetentionSettings>? _settings;
    readonly Action<InteractiveLaunch, string>? _idle;
    readonly Action<InteractiveLaunch>? _busy;
    readonly Func<InteractiveLaunch, Task<bool>>? _isIdle;

    // isIdle proves a pane is idle before the sweep closes it (sustained, not one sample).
    // idle records that a launch now waits idle for its session, so a restarted daemon may adopt it; busy
    // withdraws that record while a turn runs in the launch, so a restart never adopts a working agent as idle.
    public RetainedSessions(Action<InteractiveLaunch> stop, TimeSpan? idleTimeout = null, TimeProvider? timeProvider = null, Func<InteractiveRetentionSettings>? settings = null,
        Action<InteractiveLaunch, string>? idle = null, Action<InteractiveLaunch>? busy = null,
        Func<InteractiveLaunch, Task<bool>>? isIdle = null)
    {
        _isIdle = isIdle;
        _stop = stop;
        _idle = idle;
        _busy = busy;
        _clock = timeProvider ?? TimeProvider.System;
        _timeout = idleTimeout ?? TimeSpan.FromMinutes(5);
        _settings = settings;
        _timer = _clock.CreateTimer(_ => _ = SweepAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public void Dispose() => _timer.Dispose();

    internal void Sweep() => SweepAsync().GetAwaiter().GetResult();

    internal async Task SweepAsync()
    {
        var settings = _settings?.Invoke();
        var timeout = settings?.IdleTimeout() ?? _timeout;
        var maxRetained = settings?.MaxRetainedSessions ?? MaxRetained;
        var candidates = new List<(string Id, InteractiveLaunch Launch, bool Absolute)>();
        lock (_gate)
        {
            foreach (var pair in _sessions)
            {
                if (_reserved.ContainsKey(pair.Key)) { continue; }
                var absolute = _clock.GetElapsedTime(pair.Value.Born) >= AbsoluteLifetime;
                if (absolute || timeout >= TimeSpan.Zero && _clock.GetElapsedTime(pair.Value.IdleSince) >= timeout)
                {
                    candidates.Add((pair.Key, pair.Value.Launch, absolute));
                }
            }
            var rest = _sessions.Where(pair => !_reserved.ContainsKey(pair.Key) && candidates.All(c => c.Id != pair.Key))
                .OrderBy(pair => pair.Value.Order).ToArray();
            candidates.AddRange(rest.Take(Math.Max(0, rest.Length - maxRetained)).Select(pair => (pair.Key, pair.Value.Launch, false)));
        }
        // The probe runs outside the lock: a pane that is still working is kept and re-checked later.
        var expired = new List<(string Id, InteractiveLaunch Launch)>();
        foreach (var (id, launch, absolute) in candidates)
        {
            var idle = absolute || await IsIdleAsync(launch);
            lock (_gate)
            {
                if (_reserved.ContainsKey(id) || !_sessions.TryGetValue(id, out var current) || !ReferenceEquals(current.Launch, launch)) { continue; }
                if (idle) { _sessions.Remove(id); expired.Add((id, launch)); }
                else { _sessions[id] = current with { IdleSince = _clock.GetTimestamp() }; }
            }
        }
        CloseBestEffort(expired);
    }

    // Without a probe the retention timer alone decides; a failing probe is never proof of idleness.
    async Task<bool> IsIdleAsync(InteractiveLaunch launch)
    {
        if (_isIdle is null) { return true; }
        try { return await _isIdle(launch); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    /// <summary>Probes a retained, unreserved session's pane; a session mid-turn is busy, an unknown one unverified.</summary>
    internal async Task<SessionIdleState> ProbeAsync(string sessionId, Func<InteractiveLaunch, Task<SessionIdleState>> probe)
    {
        InteractiveLaunch launch;
        lock (_gate)
        {
            if (_reserved.ContainsKey(sessionId)) { return SessionIdleState.Busy; }
            if (!_sessions.TryGetValue(sessionId, out var entry)) { return SessionIdleState.Unverified; }
            launch = entry.Launch;
        }
        try { return await probe(launch); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return SessionIdleState.Unverified; }
    }

    public int Count { get { lock (_gate) { return _sessions.Count; } } }

    public bool IsAlive(string sessionId, Func<InteractiveLaunch, bool> isAlive)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var entry)) { return false; }
            if (isAlive(entry.Launch)) { return true; }
            _sessions.Remove(sessionId);
            Closed(entry.Launch);
            return false;
        }
    }

    public bool TryTake(string sessionId, out InteractiveLaunch launch)
    {
        lock (_gate)
        {
            if (_reserved.ContainsKey(sessionId)) { launch = null!; return false; }
            if (_sessions.Remove(sessionId, out var entry))
            {
                launch = entry.Launch;
                return true;
            }
        }
        launch = null!;
        return false;
    }

    internal bool TakeForNativeTurn(string sessionId, bool running = false)
    {
        lock (_gate)
        {
            if (_reserved.ContainsKey(sessionId)) { return false; }
            var retained = _sessions.Remove(sessionId, out var entry);
            if (!retained && !running) { return false; }
            _reserved.Add(sessionId, retained ? entry.Launch : null);
            if (retained) { _busy?.Invoke(entry.Launch); }
            return true;
        }
    }

    /// <summary>
    /// A turn from before a daemon restart may still run in <paramref name="launch"/>: protect it like a native
    /// turn reservation (never swept, taken or closed as idle) until that turn settles or is released.
    /// </summary>
    internal void Hold(string sessionId, InteractiveLaunch launch)
    {
        lock (_gate)
        {
            if (_sessions.Remove(sessionId, out var adopted)) { launch = adopted.Launch; }
            _reserved[sessionId] = launch;
            Track(sessionId, launch);
            _busy?.Invoke(launch);
        }
    }

    internal void RememberNativeTurn(string sessionId)
    {
        InteractiveLaunch? launch;
        lock (_gate)
        {
            if (!_reserved.Remove(sessionId, out launch)) { return; }
            // Native completion is settlement evidence. A running-parent reservation
            // can use the bound launch, but a revert must never do that.
            launch ??= _known.TryGetValue(sessionId, out var known) && known.Live ? known.Launch : null;
        }
        if (launch is not null) { Remember(sessionId, launch); }
    }

    internal void ReleaseNativeTurn(string sessionId)
    {
        InteractiveLaunch? launch;
        lock (_gate) { if (!_reserved.Remove(sessionId, out launch)) { return; } }
        if (launch is not null) { Remember(sessionId, launch); }
    }

    internal void Track(string sessionId, InteractiveLaunch launch)
    {
        lock (_gate) { _known[sessionId] = (launch, true); }
    }

    internal bool? Liveness(string sessionId)
    {
        lock (_gate)
        {
            if (_known.TryGetValue(sessionId, out var known)) { return known.Live; }
            return _reserved.ContainsKey(sessionId) ? true : null;
        }
    }

    internal void Closed(InteractiveLaunch launch)
    {
        lock (_gate)
        {
            foreach (var id in _known.Where(p => ReferenceEquals(p.Value.Launch, launch)).Select(p => p.Key).ToArray())
            {
                _known[id] = (launch, false);
            }
        }
    }

    public void Remember(string sessionId, InteractiveLaunch launch)
    {
        var evicted = new List<(string Id, InteractiveLaunch Launch)>();
        lock (_gate)
        {
            Track(sessionId, launch);
            if (_reserved.TryGetValue(sessionId, out var reserved))
            {
                // A running parent's own settlement may race native submission.
                // Record that settlement, but keep it protected until release.
                if (reserved is null) { _sessions[sessionId] = (launch, _next++, _clock.GetTimestamp(), _clock.GetTimestamp()); }
                return;
            }
            // The agent name is the pane's durable identity: another launch object for the
            // same pane (e.g. restart recovery) replaces the entry but never closes that pane.
            if (_sessions.TryGetValue(sessionId, out var replaced) && replaced.Launch.AgentName != launch.AgentName)
            {
                evicted.Add((sessionId, replaced.Launch));
            }
            _sessions[sessionId] = (launch, _next++, _clock.GetTimestamp(), _clock.GetTimestamp());
            _idle?.Invoke(launch, sessionId);
        }
        CloseBestEffort(evicted);
        // Cap and zero-timeout cleanup use the same idle proof as the timer; a working pane stays protected.
        _ = SweepAsync();
    }

    void CloseBestEffort(List<(string Id, InteractiveLaunch Launch)> evicted)
    {
        foreach (var (id, old) in evicted)
        {
            // Best effort: this runs from a settling turn, which must not fail on cleanup.
            try { _stop(old); Closed(old); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lock (_gate) { _sessions.TryAdd(id, (old, _next++, _clock.GetTimestamp(), _clock.GetTimestamp())); }
            }
        }
    }

    public bool Stop(string sessionId)
    {
        if (!TryTake(sessionId, out var launch))
        {
            return false;
        }
        try { _stop(launch); Closed(launch); }
        catch
        {
            lock (_gate) { _sessions.TryAdd(sessionId, (launch, _next++, _clock.GetTimestamp(), _clock.GetTimestamp())); }
            throw;
        }
        return true;
    }

    public void ForgetJobs(IReadOnlyList<string> jobIds)
    {
        lock (_gate)
        {
            foreach (var id in _known.Where(p => p.Value.Launch.JobId is { } jobId && jobIds.Contains(jobId)).Select(p => p.Key).ToArray())
            {
                _known[id] = (_known[id].Launch, false);
                _reserved.Remove(id);
            }
            foreach (var id in _sessions.Where(p => p.Value.Launch.JobId is { } jobId && jobIds.Contains(jobId)).Select(p => p.Key).ToArray())
            {
                _sessions.Remove(id);
            }
        }
    }

    public void StopAll()
    {
        string[] ids;
        lock (_gate) { ids = [.. _sessions.Keys]; }
        Exception? first = null;
        foreach (var id in ids)
        {
            try { Stop(id); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { first ??= ex; }
        }
        if (first is not null)
        {
            throw first;
        }
    }
}
