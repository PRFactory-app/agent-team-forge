using AgentTeamForge.Host.Features.PRFactory;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class WorkspaceWiringTests
{
    [Fact]
    public async Task Team_runs_in_owned_checkouts_children_integrate_then_lead_finalizes_before_completion()
    {
        using var h = new ChainHarness(new PRFactoryWorkItem
        {
            Id = Guid.NewGuid(),
            Type = "Implementation",
            RepositoryId = Guid.NewGuid(),
            LeaseToken = Guid.NewGuid(),
            AgentType = PRFactoryAgentType.Codex,
            Prompt = "Implement",
            TeamPlan = new PRFactoryTeamPlan
            {
                RecipeName = "pair",
                MaxConcurrentChildren = 1,
                Members =
                [
                    new PRFactoryTeamMember { Name = "lead", IsLead = true },
                    new PRFactoryTeamMember { Name = "writer", Role = "Write", Order = 1 }
                ]
            }
        });
        File.WriteAllText(Path.Combine(h.Repo, "base.txt"), "user's dirty checkout");

        await h.TickAsync();
        var workspace = h.Workspaces.Get($"{ChainServer.Url}|{h.Server.Item.Id:D}")!;
        Assert.Equal(h.BaseSha, workspace.StartingSha);
        Assert.Equal($"prfactory/{h.Server.Item.Id}", workspace.PublishBranch);
        var first = h.RunQueued(job =>
        {
            Assert.Null(job.WorktreePath); // Already isolated; no second worktree from incidental HEAD.
            Assert.Equal("base", File.ReadAllText(Path.Combine(job.Cwd!, "base.txt")));
            ChainHarness.Commit(job.Cwd!, job.Cwd == workspace.LeadPath ? "lead.txt" : "child.txt", "work");
        });
        Assert.Equal([workspace.LeadPath, workspace.Members[0].Path], first.Select(j => j.Cwd!).OrderBy(p => p != workspace.LeadPath));

        await h.TickAsync(); // Integrate the child and resume the lead once.
        Assert.True(File.Exists(Path.Combine(workspace.LeadPath, "child.txt")));
        Assert.Empty(h.Server.Completions);
        var finalization = Assert.Single(h.RunQueued(job => ChainHarness.Commit(job.Cwd!, "final.txt", "reviewed")));
        Assert.Equal(workspace.LeadPath, finalization.Cwd);

        await h.TickAsync();
        Assert.Single(h.Server.Completions);
        Assert.Equal("completed", h.Teams.Get(ChainServer.Url, h.Server.Item.Id)!.State);
        Assert.Equal("user's dirty checkout", File.ReadAllText(Path.Combine(h.Repo, "base.txt")));
    }
}
