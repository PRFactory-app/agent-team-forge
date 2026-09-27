using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class AccountAdmissionTests
{
    [Fact]
    public void Parses_reported_iso_reset_in_backend_error()
    {
        var signal = AccountLimitDetector.Inspect("codex", "default", "cli_nonzero_exit",
            "usage limit reached; resets at 2026-09-28T12:30:00Z", DateTimeOffset.UtcNow);
        Assert.Equal(DateTimeOffset.Parse("2026-09-28T12:30:00Z"), signal!.ResetsAt);
    }

    [Fact]
    public void Terminal_limit_blocks_account_until_reset_without_resuming_failed_job()
    {
        using var fixture = new JobFixture();
        var now = DateTimeOffset.Parse("2026-09-27T18:04:00Z");
        var reset = DateTimeOffset.Parse("2026-09-27T18:20:00Z");
        var admission = new AccountAdmission(new AccountWindowStore(fixture.Database));

        Assert.True(admission.BlockIfLimited("claude", "default", "agent_rate_limited",
            "usage limit reached; resets at 2026-09-27T18:20:00Z", now));
        var restarted = new AccountAdmission(new AccountWindowStore(JobDatabase.Open(fixture.DatabasePath, fixture.Limits.BusyTimeout)));
        Assert.False(restarted.CanStart("claude", "default", reset.AddTicks(-1)));
        Assert.True(restarted.CanStart("claude", "default", reset));
        Assert.True(restarted.CanStart("codex", "default", now));
        Assert.Empty(restarted.Due(reset));
    }

    [Fact]
    public void Quota_error_parks_original_job_without_failing_team_and_blocks_only_its_account()
    {
        using var fixture = new JobFixture();
        var teams = new PRFactoryTeamStore(fixture.Database);
        var teamId = Guid.NewGuid();
        teams.CreateIfAbsent("local", teamId, "{}");
        var admission = new AccountAdmission(new AccountWindowStore(fixture.Database));
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddMinutes(30);

        Assert.True(admission.ParkIfLimited("job-1", "claude", "account-a", "session-1",
            "agent_api_error", "You've hit your usage limit", now, reset));
        Assert.Equal("parked", new AccountWindowStore(fixture.Database).GetPark("job-1")!.State);
        Assert.Equal("claimed", teams.Get("local", teamId)!.State);
        Assert.False(admission.CanStart("claude", "account-a", now));
        Assert.True(admission.CanStart("claude", "account-b", now));
        Assert.True(admission.CanStart("codex", "account-a", now));
        Assert.Empty(admission.Due(now));
        Assert.False(admission.ParkIfLimited("job-2", "claude", "account-a", "session-2",
            null, "The prompt asks about a usage limit", now));
    }

    [Fact]
    public void Park_survives_restart_and_resumes_once_after_reset()
    {
        using var fixture = new JobFixture();
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddMinutes(5);
        var first = new AccountAdmission(new AccountWindowStore(fixture.Database));
        Assert.True(first.ParkIfLimited("job-1", "codex", "account-a", "session-1",
            "cli_nonzero_exit", "Usage limit reached", now, reset));

        var reopened = JobDatabase.Open(fixture.DatabasePath, fixture.Limits.BusyTimeout);
        var store = new AccountWindowStore(reopened);
        var restarted = new AccountAdmission(store);
        Assert.Equal("session-1", Assert.Single(restarted.Due(reset)).SessionId);
        var park = Assert.Single(restarted.Due(reset));
        Assert.True(restarted.TryBeginResume(park, reset));
        Assert.False(restarted.TryBeginResume(park, reset));
        Assert.Single(store.Resuming()); // A second restart reconciles this before starting another turn.
        restarted.ResumeRecorded("job-1");
        Assert.Empty(store.Resuming());
        Assert.Empty(restarted.Due(reset));
        Assert.True(restarted.CanStart("codex", "account-a", reset));
    }

    [Fact]
    public void Accepted_backlog_reservations_bound_duplicate_pollers()
    {
        using var fixture = new JobFixture();
        var first = new AccountAdmission(new AccountWindowStore(fixture.Database));
        var second = new AccountAdmission(new AccountWindowStore(JobDatabase.Open(fixture.DatabasePath, fixture.Limits.BusyTimeout)));
        var now = DateTimeOffset.UtcNow;

        var one = Assert.IsType<string>(first.ReserveClaim(2, now));
        var two = Assert.IsType<string>(second.ReserveClaim(2, now));
        Assert.Null(first.ReserveClaim(2, now));
        var teams = new PRFactoryTeamStore(fixture.Database);
        teams.CreateIfAbsent("local", Guid.NewGuid(), "{}");
        first.ReleaseClaim(one);
        Assert.Null(second.ReserveClaim(2, now)); // One accepted team plus one outstanding reservation.
        second.ReleaseClaim(two);
        Assert.NotNull(second.ReserveClaim(2, now));
    }

    [Fact]
    public void Later_shared_account_reset_blocks_earlier_park_and_unknown_reset_requires_recovery()
    {
        using var fixture = new JobFixture();
        var admission = new AccountAdmission(new AccountWindowStore(fixture.Database));
        var now = DateTimeOffset.UtcNow;
        admission.ParkIfLimited("job-1", "pi", "account-a", "session-1",
            "cli_nonzero_exit", "Rate limit reached", now, now.AddMinutes(5));
        admission.ParkIfLimited("job-2", "pi", "account-a", "session-2",
            "cli_nonzero_exit", "Rate limit reached", now, now.AddMinutes(10));
        Assert.Empty(admission.Due(now.AddMinutes(5)));
        Assert.Equal(2, admission.Due(now.AddMinutes(10)).Count);
        admission.ParkIfLimited("job-3", "pi", "account-a", "session-3",
            "cli_nonzero_exit", "Rate limit reached", now);
        Assert.Empty(admission.Due(now.AddHours(1)));
        admission.PermitAccountRecovery("pi", "account-a", now.AddHours(1));
        Assert.Equal(3, admission.Due(now.AddHours(1)).Count);
    }

    [Fact]
    public void Structured_rate_limit_error_parks_without_prose()
    {
        using var fixture = new JobFixture();
        var admission = new AccountAdmission(new AccountWindowStore(fixture.Database));
        Assert.True(admission.ParkIfLimited("job-1", "claude", "account-a", "session-1",
            "rate_limit_error", null, DateTimeOffset.UtcNow));
        Assert.Equal("parked", new AccountWindowStore(fixture.Database).GetPark("job-1")!.State);
    }

    [Fact]
    public void Readiness_does_not_claim_unknown_authentication_or_model()
    {
        var report = PRFactoryReadiness.Report(["fake"], "headless",
            new Dictionary<string, bool?>(), new Dictionary<string, bool?>(),
            new HashSet<string> { "fake" }, DateTimeOffset.UtcNow);
        Assert.True(Assert.Single(report).Installed);
        Assert.Equal("sign_in_unverified", report[0].Blocker);
    }
}
