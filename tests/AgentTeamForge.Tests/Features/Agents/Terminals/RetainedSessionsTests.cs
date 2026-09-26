using AgentTeamForge.Business.Features.Agents.Terminals;

namespace AgentTeamForge.Tests.Features.Agents.Terminals;

public sealed class RetainedSessionsTests
{
    static InteractiveLaunch Launch(int i) => new(InteractiveAgentKind.Claude, "atf" + i, "/tmp", null, null, "/tmp/b" + i);

    [Fact]
    public void Remember_past_cap_closes_oldest_idle_session()
    {
        var stopped = new List<string>();
        var sessions = new RetainedSessions(l => stopped.Add(l.AgentName));

        for (var i = 0; i <= RetainedSessions.MaxRetained; i++)
        {
            sessions.Remember("s" + i, Launch(i));
        }

        Assert.Equal(RetainedSessions.MaxRetained, sessions.Count);
        Assert.Equal(["atf0"], stopped);
        Assert.False(sessions.TryTake("s0", out _));
        Assert.True(sessions.Stop("s1"));
        Assert.Equal(["atf0", "atf1"], stopped);
    }

    [Fact]
    public void Remember_replacing_a_session_closes_the_previous_tab()
    {
        var stopped = new List<string>();
        var sessions = new RetainedSessions(l => stopped.Add(l.AgentName));

        sessions.Remember("s", Launch(1));
        sessions.Remember("s", Launch(2));

        Assert.Equal(["atf1"], stopped);
        Assert.Equal(1, sessions.Count);
    }
}
