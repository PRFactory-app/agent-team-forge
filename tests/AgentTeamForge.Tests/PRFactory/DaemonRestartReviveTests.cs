using System.Text.Json;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Host.Features.PRFactory;

namespace AgentTeamForge.Tests.PRFactory;

/// <summary>A6-6b: a daemon restart kills a headless agent; the work resumes in its session with the claim's safety mode.</summary>
public sealed class DaemonRestartReviveTests
{
    static PRFactoryWorkItem SandboxedPlan() => new()
    {
        Id = Guid.NewGuid(),
        Type = "Planning",
        RepositoryId = Guid.NewGuid(),
        ReadOnly = true,
        LeaseToken = Guid.NewGuid(),
        AgentType = PRFactoryAgentType.Codex,
        Prompt = "Plan",
        TicketArtefactFolder = "docs",
        SafetyMode = "sandboxed",
    };

    static ChainHarness Harness(string? worker = null)
    {
        var item = SandboxedPlan();
        if (worker is not null)
        {
            item.TeamPlan = new PRFactoryTeamPlan { MaxConcurrentChildren = 1, Members = [new PRFactoryTeamMember { Name = worker, Role = "Reviewer", Order = 1 }] };
        }
        var h = new ChainHarness(item) { Reconcile = ReconcileStop.NoRecord };
        h.Server.SandboxSupported = true;
        return h;
    }

    static async Task Tick(ChainHarness h)
    {
        var client = h.Server.Client();
        await client.SupportsMultiRepoAsync(CancellationToken.None);
        await h.Adapter(client: client).TickAsync(ChainHarness.Machine, CancellationToken.None);
    }

    /// <summary>What restart recovery does when it kills the marked headless agent: quarantine, then fail it as gone.</summary>
    static void Restart(ChainHarness h, string jobId)
    {
        Assert.Contains(jobId, h.Store.QuarantineUncertainAttempts());
        Assert.True(h.Store.FailUnattached(jobId));
        Assert.Equal("daemon_restart_agent_gone", h.Store.GetJob(jobId)!.ReasonCode);
    }

    static JsonElement Failure(ChainHarness h) => JsonElement.Parse(Assert.Single(h.Server.Failures));

    [Fact]
    public async Task Agent_killed_by_a_restart_resumes_once_in_its_session_with_the_same_sandbox_mode()
    {
        using var h = Harness();
        await Tick(h);
        var (lead, _) = h.StartOne();
        Assert.True(JobSafety.Read(lead.Options).RequestedSandboxed);
        Restart(h, lead.JobId);

        await Tick(h);
        await Tick(h); // Idempotent while the revived turn is queued.

        var turns = h.Teams.MemberJobs(ChainServer.Url, h.Server.Item.Id);
        Assert.Equal(2, turns.Count);
        var revived = h.Store.GetJob(turns.Single(id => id != lead.JobId))!;
        Assert.Equal(lead.JobId, revived.ParentJobId);
        Assert.Equal("prf-revive:" + lead.JobId, revived.IdempotencyKey);
        Assert.Contains(";sandbox=1", revived.Options);
        Assert.Equal(JobSafety.Read(lead.Options), JobSafety.Read(revived.Options));
        Assert.Empty(h.Server.Failures);
        Assert.Single(h.Server.AcceptBodies); // Same claim: no new acceptance, so no chance of a different mode.

        h.RunQueued(job => ChainHarness.Commit(job.Cwd!, "docs/plan.md", "# Plan"));
        await Tick(h);
        Assert.Single(h.Server.Completions);
        Assert.Empty(h.Server.Failures);
    }

    [Fact]
    public async Task A_second_restart_after_the_revive_reports_a_retryable_failure()
    {
        using var h = Harness();
        await Tick(h);
        var (lead, _) = h.StartOne();
        Restart(h, lead.JobId);
        await Tick(h);
        var (revived, _) = h.StartOne();
        Restart(h, revived.JobId);

        await Tick(h);
        await Tick(h);

        Assert.Equal(2, h.Teams.MemberJobs(ChainServer.Url, h.Server.Item.Id).Count); // No second revive.
        var failure = Failure(h);
        Assert.StartsWith("daemon_restart_agent_gone", failure.GetProperty("errorMessage").GetString(), StringComparison.Ordinal);
        Assert.True(failure.GetProperty("shouldRetry").GetBoolean()); // PRFactory re-queues; the re-claim reads the current mode.
    }

    [Fact]
    public async Task A_refused_revive_reports_failure_once_and_is_not_retried_every_tick()
    {
        using var h = Harness();
        await Tick(h);
        // Killed before the agent reported its session: there is nothing to resume.
        var claim = h.Store.BeginNextAttempt()!;
        Restart(h, claim.Job.JobId);

        await Tick(h);
        await Tick(h);

        Assert.Single(h.Teams.MemberJobs(ChainServer.Url, h.Server.Item.Id));
        Assert.True(h.Teams.ReviveRefused(claim.Job.JobId));
        Assert.True(Failure(h).GetProperty("shouldRetry").GetBoolean());
    }

    [Fact]
    public async Task A_member_killed_by_the_operator_after_the_restart_is_not_revived()
    {
        using var h = Harness();
        await Tick(h);
        var (lead, _) = h.StartOne();
        Restart(h, lead.JobId);
        h.Server.Commands.Add(new(Guid.NewGuid(), "KillAgent", "lead", null));

        await Tick(h);
        await Tick(h);

        Assert.True(h.Teams.IsManagedKilled(ChainServer.Url, h.Server.Item.Id, "lead"));
        Assert.Single(h.Teams.MemberJobs(ChainServer.Url, h.Server.Item.Id)); // No revive turn.
        Assert.False(Failure(h).GetProperty("shouldRetry").GetBoolean(), string.Join('\n', h.Logs));
    }

    [Fact]
    public async Task A_kill_wins_over_a_revive_intent_persisted_before_it()
    {
        using var h = Harness();
        await Tick(h);
        var (lead, _) = h.StartOne();
        Restart(h, lead.JobId);
        h.Teams.SetReviveIntent(lead.JobId, true); // A crash after the intent, before the follow-up.
        h.Teams.MarkManagedKilled(ChainServer.Url, h.Server.Item.Id, "lead");

        await Tick(h);
        await Tick(h);

        Assert.Single(h.Teams.MemberJobs(ChainServer.Url, h.Server.Item.Id));
        Assert.False(Failure(h).GetProperty("shouldRetry").GetBoolean(), string.Join('\n', h.Logs));
    }

    [Theory]
    [InlineData("a-worker")] // Sorts before "lead".
    [InlineData("z-worker")] // Sorts after "lead".
    public async Task A_stopped_lead_keeps_the_failure_terminal_whatever_the_member_order(string worker)
    {
        using var h = Harness(worker);
        await Tick(h);
        var (lead, _) = h.StartOne();
        Assert.Null(h.Stop.Execute(lead.JobId).Error); // The user stops the lead.
        var claim = h.Store.BeginNextAttempt()!; // The worker starts without a session, so its revive is refused.
        Restart(h, claim.Job.JobId);

        await Tick(h);
        await Tick(h);

        var failure = Failure(h);
        Assert.False(failure.GetProperty("shouldRetry").GetBoolean(), string.Join('\n', h.Logs));
        Assert.DoesNotContain("daemon_restart_agent_gone", failure.GetProperty("errorMessage").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_durable_refusal_is_not_replayed_when_its_intent_survived_a_crash()
    {
        using var h = Harness();
        await Tick(h);
        var claim = h.Store.BeginNextAttempt()!;
        Restart(h, claim.Job.JobId);
        h.Teams.RefuseRevive(claim.Job.JobId);
        h.Teams.SetReviveIntent(claim.Job.JobId, true); // The crash boundary the old two-write refusal allowed.

        await Tick(h);
        await Tick(h);

        Assert.DoesNotContain(h.Logs, l => l.Contains("revive refused", StringComparison.Ordinal)); // No second follow-up.
        Assert.Single(h.Teams.MemberJobs(ChainServer.Url, h.Server.Item.Id));
        Assert.True(Failure(h).GetProperty("shouldRetry").GetBoolean(), string.Join('\n', h.Logs));
    }

    [Fact]
    public async Task Refusing_a_revive_drops_its_intent_in_the_same_write()
    {
        using var h = Harness();
        await Tick(h);
        var job = h.Store.BeginNextAttempt()!.Job.JobId;
        h.Teams.SetReviveIntent(job, true);

        h.Teams.RefuseRevive(job);

        Assert.True(h.Teams.ReviveRefused(job));
        Assert.False(h.Teams.HasReviveIntent(job));
    }

    [Fact]
    public async Task A_slow_workspace_validation_defers_the_tick_and_the_revive_follows()
    {
        using var h = Harness();
        await Tick(h);
        var (lead, _) = h.StartOne();
        Restart(h, lead.JobId);

        JobWorktree.ReaderDelay.Value = TimeSpan.FromSeconds(11); // Longer than the 10 s query deadline, as a loaded machine can be.
        try { await Tick(h); }
        finally { JobWorktree.ReaderDelay.Value = TimeSpan.Zero; }

        Assert.Empty(h.Server.Failures);
        Assert.Contains(h.Logs, l => l.Contains(nameof(WorkspaceCheckIncompleteException), StringComparison.Ordinal));

        await Tick(h);

        Assert.Empty(h.Server.Failures);
        Assert.Equal(2, h.Teams.MemberJobs(ChainServer.Url, h.Server.Item.Id).Count);
    }
}
