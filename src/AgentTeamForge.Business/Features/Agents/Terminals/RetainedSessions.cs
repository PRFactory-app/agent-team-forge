namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>
/// Idle interactive sessions this daemon keeps open for a follow-up or Stop agent.
/// Past <see cref="MaxRetained"/> the oldest tab is closed; failed cleanup remains retryable.
/// </summary>
internal sealed class RetainedSessions : IDisposable
{
    public const int MaxRetained = 16;
    readonly Lock _gate = new();
    readonly Dictionary<string, (InteractiveLaunch Launch, long Order, long IdleSince)> _sessions = [];
    readonly HashSet<string> _nativeTurns = [];
    long _next;
    readonly Action<InteractiveLaunch> _stop;
    readonly TimeProvider _clock;
    readonly TimeSpan _timeout;
    readonly ITimer? _timer;

    public RetainedSessions(Action<InteractiveLaunch> stop, TimeSpan? idleTimeout = null, TimeProvider? timeProvider = null)
    {
        _stop = stop;
        _clock = timeProvider ?? TimeProvider.System;
        _timeout = idleTimeout ?? TimeSpan.FromMinutes(5);
        if (_timeout >= TimeSpan.Zero)
        {
            _timer = _clock.CreateTimer(_ => Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
    }

    public void Dispose() => _timer?.Dispose();

    internal void Sweep()
    {
        var expired = new List<(string Id, InteractiveLaunch Launch)>();
        lock (_gate)
        {
            foreach (var pair in _sessions.ToArray())
            {
                if (_timeout >= TimeSpan.Zero && _clock.GetElapsedTime(pair.Value.IdleSince) >= _timeout)
                {
                    _sessions.Remove(pair.Key);
                    expired.Add((pair.Key, pair.Value.Launch));
                }
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
            return false;
        }
    }

    public bool TryTake(string sessionId, out InteractiveLaunch launch)
    {
        lock (_gate)
        {
            _nativeTurns.Remove(sessionId);
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
            if (!_sessions.Remove(sessionId) && !running) { return false; }
            _nativeTurns.Add(sessionId);
            return true;
        }
    }

    internal void RememberNativeTurn(string sessionId, InteractiveLaunch launch)
    {
        lock (_gate) { _nativeTurns.Remove(sessionId); }
        Remember(sessionId, launch);
    }

    public void Remember(string sessionId, InteractiveLaunch launch)
    {
        var evicted = new List<(string Id, InteractiveLaunch Launch)>();
        lock (_gate)
        {
            if (_nativeTurns.Contains(sessionId)) { return; }
            if (_sessions.TryGetValue(sessionId, out var replaced) && !ReferenceEquals(replaced.Launch, launch))
            {
                evicted.Add((sessionId, replaced.Launch));
            }
            _sessions[sessionId] = (launch, _next++, _clock.GetTimestamp());
            while (_sessions.Count > MaxRetained || _timeout == TimeSpan.Zero && _sessions.Count > 0)
            {
                var oldest = _sessions.MinBy(pair => pair.Value.Order);
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
            try { _stop(old); }
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
        try { _stop(launch); }
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
