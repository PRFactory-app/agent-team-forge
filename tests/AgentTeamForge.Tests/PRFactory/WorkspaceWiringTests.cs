using System.Text.Json;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Host.Features.PRFactory;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class WorkspaceWiringTests
{
    [Theory]
    [InlineData("CodeReview", "implementation")]
    [InlineData("Implementation", "review-fixes")]
    public async Task Continuation_uses_exact_accepted_head_before_handover_even_when_remote_advances(string phase, string priorBranch)
    {
        using var h = new ChainHarness(Item(phase));
        ChainHarness.Git(h.Repo, "checkout", "-b", priorBranch);
        var accepted = ChainHarness.Commit(h.Repo, "accepted.txt", "accepted");
        ChainHarness.Git(h.Repo, "push", "origin", priorBranch);
        ChainHarness.Commit(h.Repo, "later.txt", "later remote head");
        ChainHarness.Git(h.Repo, "push", "origin", priorBranch);
        ChainHarness.Git(h.Repo, "checkout", "-b", "handover", "main");
        var handover = ChainHarness.Commit(h.Repo, "handover.txt", "handover");
        ChainHarness.Git(h.Repo, "push", "origin", "handover");
        h.Server.Item.Continuation = new(priorBranch, accepted);
        h.Server.Item.StartFromBranch = "handover";
        h.Server.Item.StartCommitSha = handover;

        await h.TickAsync();

        var workspace = h.Workspaces.Get($"{ChainServer.Url}|{h.Server.Item.Id:D}")!;
        Assert.Equal(accepted, workspace.StartingSha);
        Assert.Equal(accepted, JobWorktree.Head(workspace.LeadPath));
        Assert.False(File.Exists(Path.Combine(workspace.LeadPath, "later.txt")));
        Assert.Empty(h.Server.Failures);
    }

    [Fact]
    public async Task Unpublished_continuation_sha_fails_claim_with_clear_error()
    {
        using var h = new ChainHarness(Item("CodeReview"));
        ChainHarness.Git(h.Repo, "checkout", "-b", "implementation");
        var unpublished = ChainHarness.Commit(h.Repo, "local-only.txt", "not pushed");
        ChainHarness.Git(h.Repo, "push", "origin", "main:implementation");
        h.Server.Item.Continuation = new("implementation", unpublished);

        await h.TickAsync();

        Assert.Null(h.Workspaces.Get($"{ChainServer.Url}|{h.Server.Item.Id:D}"));
        Assert.Contains("not on remote branch implementation", Assert.Single(h.Server.Failures));
    }

    [Fact]
    public async Task Handover_claim_uses_its_sha_and_fails_if_sha_is_missing()
    {
        using var h = new ChainHarness(Item("Implementation"));
        ChainHarness.Git(h.Repo, "checkout", "-b", "handover");
        var accepted = ChainHarness.Commit(h.Repo, "handover.txt", "accepted");
        ChainHarness.Git(h.Repo, "push", "origin", "handover");
        h.Server.Item.StartFromBranch = "handover";
        h.Server.Item.StartCommitSha = accepted;
        await h.TickAsync();
        Assert.Equal(accepted, h.Workspaces.Get($"{ChainServer.Url}|{h.Server.Item.Id:D}")!.StartingSha);

        using var missing = new ChainHarness(Item("Implementation"));
        ChainHarness.Git(missing.Repo, "checkout", "-b", "handover");
        ChainHarness.Git(missing.Repo, "push", "origin", "handover");
        missing.Server.Item.StartFromBranch = "handover";
        await missing.TickAsync();
        Assert.Contains("authoritative starting SHA", Assert.Single(missing.Server.Failures));
    }

    [Fact]
    public async Task Base_snapshot_uses_recorded_commit_and_legacy_claim_uses_current_default()
    {
        using var pinned = new ChainHarness(Item("Planning"));
        ChainHarness.Commit(pinned.Repo, "new-base.txt", "base advanced");
        ChainHarness.Git(pinned.Repo, "push", "origin", "main");
        pinned.Server.Item.BaseSnapshot = new("main", pinned.BaseSha);
        await pinned.TickAsync();
        var recorded = pinned.Workspaces.Get($"{ChainServer.Url}|{pinned.Server.Item.Id:D}")!;
        Assert.Equal(pinned.BaseSha, recorded.BaseSha);
        Assert.Equal(pinned.BaseSha, recorded.StartingSha);

        using var legacy = new ChainHarness(Item("Planning"));
        var latest = ChainHarness.Commit(legacy.Repo, "new-base.txt", "base advanced");
        ChainHarness.Git(legacy.Repo, "push", "origin", "main");
        await legacy.TickAsync();
        var current = legacy.Workspaces.Get($"{ChainServer.Url}|{legacy.Server.Item.Id:D}")!;
        Assert.Equal(latest, current.BaseSha);
        Assert.Equal(latest, current.StartingSha);
    }

    [Fact]
    public async Task Legacy_project_init_claim_resumes_its_publish_branch_without_a_start_sha()
    {
        var item = Item("Implementation");
        item.TicketSource = "ProjectInit";
        item.TicketKey = "NIM-1";
        item.PublishBranch = "init/NIM-1";
        item.StartFromBranch = "init/NIM-1";
        using var h = new ChainHarness(item);
        ChainHarness.Git(h.Repo, "checkout", "-b", "init/NIM-1");
        var published = ChainHarness.Commit(h.Repo, "init.txt", "earlier step");
        ChainHarness.Git(h.Repo, "push", "origin", "init/NIM-1");

        await h.TickAsync();

        Assert.Empty(h.Server.Failures);
        Assert.Equal(published, h.Workspaces.Get($"{ChainServer.Url}|{h.Server.Item.Id:D}")!.StartingSha);
    }

    [Fact]
    public void Claim_wire_round_trips_workspace_continuity_fields()
    {
        const string json = """
            {"startFromBranch":"handover","startCommitSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
             "continuation":{"branch":"implementation","commitSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"},
             "baseSnapshot":{"branch":"main","commitSha":"cccccccccccccccccccccccccccccccccccccccc"}}
            """;
        var claim = JsonSerializer.Deserialize(json, PRFactoryWorkItemJson.Default.PRFactoryWorkItem)!;
        Assert.Equal(new PRFactoryWorkspaceRevision("implementation", new string('b', 40)), claim.Continuation);
        Assert.Equal(new PRFactoryWorkspaceRevision("main", new string('c', 40)), claim.BaseSnapshot);
        Assert.Equal(new string('a', 40), claim.StartCommitSha);
        var legacy = JsonSerializer.Deserialize("{}", PRFactoryWorkItemJson.Default.PRFactoryWorkItem)!;
        Assert.Null(legacy.Continuation);
        Assert.Null(legacy.BaseSnapshot);
        Assert.Null(legacy.StartCommitSha);
    }

    static PRFactoryWorkItem Item(string phase) => new()
    {
        Id = Guid.NewGuid(),
        Type = phase,
        RepositoryId = Guid.NewGuid(),
        LeaseToken = Guid.NewGuid(),
        AgentType = PRFactoryAgentType.Codex,
        Prompt = phase,
        TicketArtefactFolder = "ticket",
        ReadOnly = true
    };

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
            TicketArtefactFolder = "ticket",
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
            Directory.CreateDirectory(Path.Combine(job.Cwd!, "ticket"));
            File.WriteAllText(Path.Combine(job.Cwd!, "ticket/report.md"), "# Report");
        });
        Assert.Equal([workspace.LeadPath, workspace.Members[0].Path], first.Select(j => j.Cwd!).OrderBy(p => p != workspace.LeadPath));

        await h.TickAsync(); // Integrate the child and resume the lead once.
        Assert.True(File.Exists(Path.Combine(workspace.LeadPath, "child.txt")));
        Assert.True(File.Exists(Path.Combine(workspace.StagingPath, "child-0/ticket/report.md")));
        Assert.Empty(h.Server.Completions);
        var finalization = Assert.Single(h.RunQueued(job => ChainHarness.Commit(job.Cwd!, "final.txt", "reviewed")));
        Assert.Equal(workspace.LeadPath, finalization.Cwd);

        await h.TickAsync();
        Assert.Single(h.Server.Completions);
        Assert.Equal("completed", h.Teams.Get(ChainServer.Url, h.Server.Item.Id)!.State);
        Assert.Equal("user's dirty checkout", File.ReadAllText(Path.Combine(h.Repo, "base.txt")));
    }
}
