using System.Collections.Concurrent;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>
/// Capped exponential backoff for a WIP publication that keeps failing for the same head, so a persistent
/// failure is retried (and logged) once per step instead of every heartbeat. A new head retries at once.
/// </summary>
public sealed class WipBackoff(TimeSpan first, TimeSpan max)
{
    readonly ConcurrentDictionary<string, (string Head, int Failures, DateTimeOffset NextAt)> failures = [];

    public bool Due(string key, string head, DateTimeOffset now) =>
        !failures.TryGetValue(key, out var last) || last.Head != head || now >= last.NextAt;

    /// <summary>Record a failed attempt and return the delay before the next one.</summary>
    public TimeSpan Failed(string key, string head, DateTimeOffset now)
    {
        var count = failures.TryGetValue(key, out var last) && last.Head == head ? last.Failures + 1 : 1;
        var delay = TimeSpan.FromTicks(Math.Min(max.Ticks, first.Ticks * (1L << Math.Min(count - 1, 20))));
        failures[key] = (head, count, now + delay);
        return delay;
    }

    public void Succeeded(string key) => failures.TryRemove(key, out _);
}
