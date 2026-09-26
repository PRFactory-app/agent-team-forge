namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>
/// Idle interactive sessions this daemon keeps open for a follow-up or Stop agent.
/// Bounded: past <see cref="MaxRetained"/> the least recently retained tab is closed.
/// </summary>
internal sealed class RetainedSessions(Action<InteractiveLaunch> stop)
{
    public const int MaxRetained = 16;
    readonly Lock _gate = new();
    readonly Dictionary<string, (InteractiveLaunch Launch, long Order)> _sessions = [];
    long _next;

    public int Count { get { lock (_gate) { return _sessions.Count; } } }

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
        var evicted = new List<InteractiveLaunch>();
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var replaced) && !ReferenceEquals(replaced.Launch, launch))
            {
                evicted.Add(replaced.Launch);
            }
            _sessions[sessionId] = (launch, _next++);
            while (_sessions.Count > MaxRetained)
            {
                var oldest = _sessions.MinBy(pair => pair.Value.Order);
                _sessions.Remove(oldest.Key);
                evicted.Add(oldest.Value.Launch);
            }
        }
        foreach (var old in evicted)
        {
            // Best effort: this runs from a settling turn, which must not fail on cleanup.
            try { stop(old); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
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
