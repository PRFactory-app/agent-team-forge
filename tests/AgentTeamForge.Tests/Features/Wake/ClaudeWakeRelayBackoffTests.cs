using AgentTeamForge.Host.Features.Wake;

namespace AgentTeamForge.Tests.Features.Wake;

public sealed class ClaudeWakeRelayBackoffTests
{
    [Fact]
    public void Idle_polling_backs_off_to_the_cap_and_resets_when_busy()
    {
        var delay = ClaudeWakeRelay.MinPoll;
        var total = TimeSpan.Zero;
        for (var i = 0; i < 10; i++)
        {
            delay = ClaudeWakeRelay.NextDelay(delay, busy: false);
            Assert.True(delay <= ClaudeWakeRelay.MaxPoll);
            total += delay;
        }
        Assert.Equal(ClaudeWakeRelay.MaxPoll, delay);
        Assert.True(ClaudeWakeRelay.MaxPoll <= TimeSpan.FromSeconds(3));
        Assert.Equal(ClaudeWakeRelay.MinPoll, ClaudeWakeRelay.NextDelay(delay, busy: true));
    }
}
