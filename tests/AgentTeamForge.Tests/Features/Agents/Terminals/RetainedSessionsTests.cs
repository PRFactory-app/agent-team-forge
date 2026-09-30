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
    [Fact]
    public void Timeout_closes_idle_session_using_injected_timer()
    {
        using var clock = new ManualClock();
        var stopped = new List<InteractiveLaunch>();
        using var sessions = new RetainedSessions(stopped.Add, TimeSpan.FromMinutes(5), clock);
        var launch = Launch(1);
        sessions.Remember("s", launch);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Empty(stopped);
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(30));
        Assert.Equal([launch], stopped);
        Assert.False(sessions.IsAlive("s", _ => true));
    }

    [Fact]
    public void Taken_session_is_not_closed_by_timeout()
    {
        using var clock = new ManualClock();
        var stopped = new List<InteractiveLaunch>();
        using var sessions = new RetainedSessions(stopped.Add, TimeSpan.FromMinutes(5), clock);
        sessions.Remember("s", Launch(1));
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True(sessions.TryTake("s", out _));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(stopped);

        var native = Launch(2);
        sessions.Remember("native", native);
        Assert.True(sessions.TakeForNativeTurn("native"));
        sessions.Remember("native", native); // The prior run may still unwind during native delivery.
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Empty(stopped);
        sessions.RememberNativeTurn("native");
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal([native], stopped);
    }

    [Fact]
    public void Zero_timeout_closes_without_retaining()
    {
        var stopped = new List<InteractiveLaunch>();
        using var sessions = new RetainedSessions(stopped.Add, TimeSpan.Zero);
        var launch = Launch(1);
        sessions.Remember("s", launch);
        Assert.Equal([launch], stopped);
        Assert.Equal(0, sessions.Count);
        Assert.False(sessions.TryTake("s", out _));
    }

    [Fact]
    public void Releasing_a_running_parent_reservation_does_not_make_it_idle()
    {
        var stopped = new List<InteractiveLaunch>();
        using var sessions = new RetainedSessions(stopped.Add, TimeSpan.Zero);
        var launch = Launch(1);
        sessions.Track("working", launch);
        Assert.True(sessions.TakeForNativeTurn("working", running: true));
        sessions.ReleaseNativeTurn("working");
        sessions.ReleaseNativeTurn("working");
        sessions.Sweep();
        Assert.Equal(0, sessions.Count);
        Assert.Empty(stopped);
        Assert.True(sessions.Liveness("working"));
        sessions.Remember("working", launch); // Only the parent's own settlement makes it idle.
        Assert.Equal([launch], stopped);
        Assert.False(sessions.Liveness("working"));

        sessions.Track("settling", launch);
        Assert.True(sessions.TakeForNativeTurn("settling", running: true));
        sessions.Remember("settling", launch); // The parent settles before the native submit returns.
        sessions.Sweep();
        Assert.Single(stopped);
        Assert.False(sessions.TryTake("settling", out _));
        sessions.ReleaseNativeTurn("settling");
        sessions.Sweep();
        Assert.Equal(2, stopped.Count);
    }

    sealed class ManualClock : TimeProvider, IDisposable
    {
        long _ticks;
        ManualTimer? _timer;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => _timer = new ManualTimer(callback, state);
        public void Dispose() => _timer?.Dispose();
        public void Advance(TimeSpan duration)
        {
            _ticks += duration.Ticks;
            _timer?.Fire();
        }
        sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            bool _disposed;
            public void Fire() { if (!_disposed) { callback(state); } }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
