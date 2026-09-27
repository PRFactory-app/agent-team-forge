using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Host.Features.PRFactory;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class AccountParkingTests
{
    [Fact]
    public async Task Limited_lead_parks_holds_completion_then_resumes_same_session_once_after_reset()
    {
        using var h = new ChainHarness(new PRFactoryWorkItem
        {
            Id = Guid.NewGuid(),
            Type = "Planning",
            RepositoryId = Guid.NewGuid(),
            ReadOnly = true,
            LeaseToken = Guid.NewGuid(),
            AgentType = PRFactoryAgentType.Codex,
            Prompt = "Plan",
            TicketArtefactFolder = "docs",
        });
        var accounts = new AccountAdmission(new AccountWindowStore(h.Database));
        h.Accounts = accounts;
        await h.TickAsync();
        var (lead, run) = h.StartOne();
        // What the dispatcher does on backend-owned limit evidence: end the turn, then park it.
        h.Store.EndUnsuccessfully(run, JobStatus.Failed, "codex_error", "usage limit reached; resets at 2026-01-01T00:00:00Z");
        var parked = h.Store.GetJob(lead.JobId)!;
        Assert.True(accounts.ParkIfLimited(parked.JobId, "codex", PRFactoryWorkItems.DefaultAccount, parked.SessionId,
            AccountAdmission.EvidenceCode("codex", "codex_error"), "usage limit reached", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1)));
        Assert.False(accounts.CanStart("codex", PRFactoryWorkItems.DefaultAccount, DateTimeOffset.UtcNow));

        await h.TickAsync();
        Assert.Empty(h.Server.Failures); // Parked, not failed.
        Assert.Single(h.Teams.MemberJobs(ChainServer.Url, h.Server.Item.Id));

        accounts.PermitAccountRecovery("codex", PRFactoryWorkItems.DefaultAccount, DateTimeOffset.UtcNow);
        // Simulate a crash after follow-up acceptance + member mapping, before the park receipt.
        Assert.True(accounts.TryBeginResume(accounts.Park(lead.JobId)!, DateTimeOffset.UtcNow));
        var accepted = h.FollowUp.Execute(new(lead.JobId,
            "The account usage limit has reset. Continue the task exactly where you left off.", "prf-resume:" + lead.JobId));
        Assert.Null(accepted.Error);
        h.Teams.RecordMember(ChainServer.Url, h.Server.Item.Id, "lead", 1, accepted.Job!.JobId);
        await h.TickAsync();
        await h.TickAsync(); // Idempotent: still exactly one resumed turn.
        var turns = h.Teams.MemberJobs(ChainServer.Url, h.Server.Item.Id);
        Assert.Equal(2, turns.Count);
        var resumed = h.Store.GetJob(turns.Single(id => id != lead.JobId))!;
        Assert.Equal(lead.JobId, resumed.ParentJobId);
        Assert.Equal("resumed", accounts.Park(lead.JobId)!.State);

        h.RunQueued(job => ChainHarness.Commit(job.Cwd!, "docs/plan.md", "# Plan"));
        await h.TickAsync();
        Assert.Single(h.Server.Completions);
        Assert.Empty(h.Server.Failures);
    }
}
