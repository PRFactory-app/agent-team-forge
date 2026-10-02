using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Host.Features.PRFactory;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class ReclaimAfterReleaseTests
{
    [Fact]
    public async Task Reclaim_after_release_starts_in_a_fresh_workspace_despite_a_dirty_kept_child()
    {
        var item = new PRFactoryWorkItem
        {
            Id = Guid.NewGuid(),
            TicketKey = "PRF-42",
            Type = "Planning",
            RepositoryId = Guid.NewGuid(),
            LeaseToken = Guid.NewGuid(),
            AgentType = PRFactoryAgentType.Codex,
            Prompt = "Plan",
            TeamPlan = new PRFactoryTeamPlan
            {
                MaxConcurrentChildren = 1,
                Members = [new PRFactoryTeamMember { Name = "qa", Role = "qa", Order = 1 }]
            }
        };
        using var h = new ChainHarness(item);
        h.Server.BaseWipSupported = true;
        var ct = TestContext.Current.CancellationToken;
        var key = $"{ChainServer.Url}|{item.Id:D}";
        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, ct);
        var old = h.Workspaces.Get(key)!;
        Assert.NotNull(h.Teams.MemberJob(ChainServer.Url, item.Id, "lead", 0));

        // The server fences the item; the daemon stops the team and acknowledges.
        h.Server.Status = 6;
        for (var i = 0; i < 3; i++) { await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, ct); }
        // The previous execution left uncommitted output in the kept child checkout.
        Directory.CreateDirectory(Path.Combine(old.Members[0].Path, "docs/PRF-42"));
        File.WriteAllText(Path.Combine(old.Members[0].Path, "docs/PRF-42/plan.md"), "# Plan");
        Assert.Equal(PRFactoryReleaseResult.Released, h.Teams.ReleaseFenced(ChainServer.Url, item.Id));

        h.Server.Requeue();
        h.Logs.Clear();
        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, ct);
        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, ct);

        Assert.DoesNotContain(h.Logs, l => l.Contains("deferred", StringComparison.Ordinal));
        var lead = h.Teams.MemberJob(ChainServer.Url, item.Id, "lead", 0);
        Assert.NotNull(lead);
        Assert.NotEqual(old.Root, h.Workspaces.Get(key)!.Root); // fresh checkout; the old one is kept on disk
        Assert.True(File.Exists(Path.Combine(old.Members[0].Path, "docs/PRF-42/plan.md")));
    }
}
