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
    [Fact]
    public void Failed_stop_retries_same_launch()
    {
        var attempts = new List<InteractiveLaunch>();
        var sessions = new RetainedSessions(l =>
        {
            attempts.Add(l);
            if (attempts.Count == 1) { throw new IOException("temporary"); }
        });
        var launch = Launch(1);
        sessions.Remember("s", launch);
        Assert.Throws<IOException>(() => sessions.Stop("s"));
        Assert.True(sessions.Stop("s"));
        Assert.Equal([launch, launch], attempts);
    }

    [Fact]
    public void Failed_eviction_remains_retryable()
    {
        var fail = true;
        var sessions = new RetainedSessions(_ => { if (fail) { throw new IOException("temporary"); } });
        for (var i = 0; i <= RetainedSessions.MaxRetained; i++) { sessions.Remember("s" + i, Launch(i)); }
        Assert.Equal(RetainedSessions.MaxRetained + 1, sessions.Count);
        fail = false;
        Assert.True(sessions.Stop("s0"));
    }

    [Fact]
    public void Failed_stop_does_not_replace_concurrently_reused_session()
    {
        RetainedSessions sessions = null!;
        var newer = Launch(2);
        sessions = new RetainedSessions(_ =>
        {
            sessions.Remember("s", newer);
            throw new IOException("temporary");
        });
        sessions.Remember("s", Launch(1));
        Assert.Throws<IOException>(() => sessions.Stop("s"));
        Assert.True(sessions.TryTake("s", out var retained));
        Assert.Same(newer, retained);
    }
}
