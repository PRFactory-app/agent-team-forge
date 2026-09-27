using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Host.Features.PRFactory;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class BaseWipHandoverTests
{
    [Fact]
    public async Task Adoption_fences_when_acceptance_release_id_differs_from_claim()
    {
        var repository = Guid.NewGuid();
        var item = new PRFactoryWorkItem
        {
            Id = Guid.NewGuid(),
            RepositoryId = repository,
            LeaseToken = Guid.NewGuid(),
            AgentType = PRFactoryAgentType.Codex,
            Prompt = "Continue",
            HandoverReleaseId = "release-1",
            HandoverRepositoryId = repository
        };
        using var h = new ChainHarness(item);
        item.HandoverBaseCommitSha = h.BaseSha;
        item.StartFromBranch = "wip/source/PRF-42";
        item.StartCommitSha = h.BaseSha;
        h.Server.BaseWipSupported = true;
        h.Server.AcceptanceReleaseIdOverride = "release-2";

        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        Assert.Empty(h.Teams.MemberJobs(ChainServer.Url, item.Id));
        Assert.Equal("reconciliation_needed", h.Teams.Get(ChainServer.Url, item.Id)!.AcceptanceState);
    }

    [Fact]
    public async Task Adoption_refuses_moved_wip_tip_even_with_a_recorded_release()
    {
        var repository = Guid.NewGuid();
        var branch = "wip/source/PRF-42";
        var item = new PRFactoryWorkItem
        {
            Id = Guid.NewGuid(),
            TicketKey = "PRF-42",
            Type = "Implementation",
            RepositoryId = repository,
            LeaseToken = Guid.NewGuid(),
            AgentType = PRFactoryAgentType.Codex,
            Prompt = "Continue",
            StartFromBranch = branch,
            HandoverReleaseId = "release-1",
            HandoverRepositoryId = repository
        };
        using var h = new ChainHarness(item);
        item.StartCommitSha = h.BaseSha;
        item.HandoverBaseCommitSha = h.BaseSha;
        ChainHarness.Git(h.Repo, "push", "origin", h.BaseSha + ":refs/heads/" + branch);
        var moved = ChainHarness.Commit(h.Repo, "later.txt", "later");
        ChainHarness.Git(h.Repo, "push", "origin", moved + ":refs/heads/" + branch);
        h.Server.BaseWipSupported = true;

        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        Assert.Empty(h.Teams.MemberJobs(ChainServer.Url, item.Id));
        Assert.Single(h.Server.Failures);
        Assert.Equal(moved, h.RemoteHead(branch));
    }

    [Fact]
    public async Task Request_waits_for_active_lead_and_dirty_files_then_releases_exact_receipt()
    {
        var item = new PRFactoryWorkItem
        {
            Id = Guid.NewGuid(),
            TicketKey = "PRF-42",
            Type = "Implementation",
            RepositoryId = Guid.NewGuid(),
            LeaseToken = Guid.NewGuid(),
            AgentType = PRFactoryAgentType.Codex,
            Prompt = "Work"
        };
        using var h = new ChainHarness(item);
        h.Server.BaseWipSupported = true;
        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        var (lead, run) = h.StartOne();
        var head = ChainHarness.Commit(lead.Cwd!, "work.txt", "committed");
        h.Server.HandoverRequested = true;
        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        Assert.Empty(h.Server.Releases);
        Assert.Equal(JobStatus.Running, h.Store.GetJob(lead.JobId)!.Status);

        var dirty = Path.Combine(lead.Cwd!, "uncommitted.txt");
        File.WriteAllText(dirty, "keep me");
        Assert.True(h.Store.Complete(run, "done"));
        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        Assert.Empty(h.Server.Releases);
        Assert.Equal("keep me", File.ReadAllText(dirty));

        File.Delete(dirty);
        var workspace = h.Workspaces.Get($"{ChainServer.Url}|{item.Id:D}")!;
        var staged = Path.Combine(workspace.StagingPath, "unreceipted.txt");
        File.WriteAllText(staged, "keep this too");
        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        Assert.Empty(h.Server.Releases);
        Assert.Equal("keep this too", File.ReadAllText(staged));
        File.Delete(staged);
        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        var report = h.Server.WipReports.Last();
        Assert.True(h.Server.WipReports.Count >= 2);
        Assert.EndsWith(":request-1", report.GetProperty("publicationId").GetString());
        Assert.Equal(head, report.GetProperty("headSha").GetString());
        var release = Assert.Single(h.Server.Releases);
        Assert.Equal(head, release.GetProperty("verifiedWipSha").GetString());
        Assert.Equal(item.RepositoryId, release.GetProperty("repositoryId").GetGuid());
        Assert.True(h.Teams.Get(ChainServer.Url, item.Id)!.State == "completed", string.Join("\n", h.Logs));
        Assert.Equal(head, new PRFactoryHandoverStore(h.Database)
            .Release($"{ChainServer.Url}|{item.Id:D}")?.VerifiedWipSha);
    }
}
