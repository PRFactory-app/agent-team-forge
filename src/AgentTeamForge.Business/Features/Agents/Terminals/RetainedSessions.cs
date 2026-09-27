namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>
/// Idle interactive sessions this daemon keeps open for a follow-up or Stop agent.
/// Past <see cref="MaxRetained"/> the oldest tab is closed; failed cleanup remains retryable.
/// </summary>
internal sealed class RetainedSessions(Action<InteractiveLaunch> stop)
{
    public const int MaxRetained = 16;
    readonly Lock _gate = new();
    readonly Dictionary<string, (InteractiveLaunch Launch, long Order)> _sessions = [];
    long _next;

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
            if (_sessions.Remove(sessionId, out var entry))
            {
                launch = entry.Launch;
                return true;
            }
        }
        launch = null!;
        return false;
    }

    public void Remember(string sessionId, InteractiveLaunch launch)
    {
        var evicted = new List<(string Id, InteractiveLaunch Launch)>();
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var replaced) && !ReferenceEquals(replaced.Launch, launch))
            {
                evicted.Add((sessionId, replaced.Launch));
            }
            _sessions[sessionId] = (launch, _next++);
            while (_sessions.Count > MaxRetained)
            {
                var oldest = _sessions.MinBy(pair => pair.Value.Order);
                _sessions.Remove(oldest.Key);
                evicted.Add((oldest.Key, oldest.Value.Launch));
            }
        }
        foreach (var (id, old) in evicted)
        {
            // Best effort: this runs from a settling turn, which must not fail on cleanup.
            try { stop(old); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lock (_gate) { _sessions.TryAdd(id, (old, _next++)); }
            }
        }
    }

    public bool Stop(string sessionId)
    {
        if (!TryTake(sessionId, out var launch))
        {
            return false;
        }
        try { stop(launch); }
        catch
        {
            lock (_gate) { _sessions.TryAdd(sessionId, (launch, _next++)); }
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
