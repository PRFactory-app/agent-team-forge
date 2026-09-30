namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>
/// Idle interactive sessions this daemon keeps open for a follow-up or Stop agent.
/// Past the configured idle timeout or cap the tab is closed; failed cleanup remains retryable.
/// </summary>
internal sealed class RetainedSessions : IDisposable
{
    public const int MaxRetained = 16;
    readonly Lock _gate = new();
    readonly Dictionary<string, (InteractiveLaunch Launch, long Order, long IdleSince)> _sessions = [];
    readonly Dictionary<string, InteractiveLaunch?> _reserved = [];
    readonly Dictionary<string, (InteractiveLaunch Launch, bool Live)> _known = [];
    long _next;
    readonly Action<InteractiveLaunch> _stop;
    readonly TimeProvider _clock;
    readonly TimeSpan _timeout;
    readonly ITimer _timer;
    readonly Func<InteractiveRetentionSettings>? _settings;

    public RetainedSessions(Action<InteractiveLaunch> stop, TimeSpan? idleTimeout = null, TimeProvider? timeProvider = null, Func<InteractiveRetentionSettings>? settings = null)
    {
        _stop = stop;
        _clock = timeProvider ?? TimeProvider.System;
        _timeout = idleTimeout ?? TimeSpan.FromMinutes(5);
        _settings = settings;
        _timer = _clock.CreateTimer(_ => Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public void Dispose() => _timer.Dispose();

    internal void Sweep()
    {
        var settings = _settings?.Invoke();
        var timeout = settings?.IdleTimeout() ?? _timeout;
        var maxRetained = settings?.MaxRetainedSessions ?? MaxRetained;
        var expired = new List<(string Id, InteractiveLaunch Launch)>();
        lock (_gate)
        {
            foreach (var pair in _sessions.ToArray())
            {
                if (!_reserved.ContainsKey(pair.Key) && timeout >= TimeSpan.Zero && _clock.GetElapsedTime(pair.Value.IdleSince) >= timeout)
                {
                    _sessions.Remove(pair.Key);
                    expired.Add((pair.Key, pair.Value.Launch));
                }
            }
            while (true)
            {
                var idle = _sessions.Where(pair => !_reserved.ContainsKey(pair.Key)).ToArray();
                if (idle.Length <= maxRetained) { break; }
                var oldest = idle.MinBy(pair => pair.Value.Order);
                _sessions.Remove(oldest.Key);
                expired.Add((oldest.Key, oldest.Value.Launch));
            }
        }
        CloseBestEffort(expired);
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
            return true;
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
        var settings = _settings?.Invoke();
        var timeout = settings?.IdleTimeout() ?? _timeout;
        var maxRetained = settings?.MaxRetainedSessions ?? MaxRetained;
        var evicted = new List<(string Id, InteractiveLaunch Launch)>();
        lock (_gate)
        {
            Track(sessionId, launch);
            if (_reserved.TryGetValue(sessionId, out var reserved))
            {
                // A running parent's own settlement may race native submission.
                // Record that settlement, but keep it protected until release.
                if (reserved is null) { _sessions[sessionId] = (launch, _next++, _clock.GetTimestamp()); }
                return;
            }
            if (_sessions.TryGetValue(sessionId, out var replaced) && !ReferenceEquals(replaced.Launch, launch))
            {
                evicted.Add((sessionId, replaced.Launch));
            }
            _sessions[sessionId] = (launch, _next++, _clock.GetTimestamp());
            while (true)
            {
                var idle = _sessions.Where(pair => !_reserved.ContainsKey(pair.Key)).ToArray();
                if (idle.Length <= maxRetained && (timeout != TimeSpan.Zero || idle.Length == 0)) { break; }
                var oldest = idle.MinBy(pair => pair.Value.Order);
                _sessions.Remove(oldest.Key);
                evicted.Add((oldest.Key, oldest.Value.Launch));
            }
        }
        CloseBestEffort(evicted);
    }

    void CloseBestEffort(List<(string Id, InteractiveLaunch Launch)> evicted)
    {
        foreach (var (id, old) in evicted)
        {
            // Best effort: this runs from a settling turn, which must not fail on cleanup.
            try { _stop(old); Closed(old); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lock (_gate) { _sessions.TryAdd(id, (old, _next++, _clock.GetTimestamp())); }
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
            lock (_gate) { _sessions.TryAdd(sessionId, (launch, _next++, _clock.GetTimestamp())); }
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
