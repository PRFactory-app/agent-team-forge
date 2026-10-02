using AgentTeamForge.Business.Features.Jobs;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class WipBackoffTests
{
    [Fact]
    public void Persistent_failure_backs_off_to_the_cap_and_a_new_head_retries_at_once()
    {
        var backoff = new WipBackoff(TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(5));
        var now = DateTimeOffset.UnixEpoch;
        var attempts = new List<DateTimeOffset>();
        // One hour of 4 s heartbeats with a push that always fails for the same head.
        for (var tick = 0; tick < 900; tick++, now += TimeSpan.FromSeconds(4))
        {
            if (backoff.Due("item", "head-a", now))
            {
                attempts.Add(now);
                backoff.Failed("item", "head-a", now);
            }
        }

        Assert.InRange(attempts.Count, 12, 20);
        var gaps = attempts.Zip(attempts.Skip(1), (a, b) => b - a).ToList();
        Assert.True(gaps.Zip(gaps.Skip(1), (a, b) => b >= a).All(x => x));
        Assert.All(gaps, gap => Assert.InRange(gap, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(4)));
        Assert.True(gaps[^1] >= TimeSpan.FromMinutes(5));

        Assert.False(backoff.Due("item", "head-a", now));
        Assert.True(backoff.Due("item", "head-b", now));
        backoff.Succeeded("item");
        Assert.True(backoff.Due("item", "head-a", now));
    }
}
