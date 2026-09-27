using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Host.Features.PRFactory;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class MultiRepositoryTests
{
    [Fact]
    public async Task Repository_set_refuses_missing_mapping_and_aliases_before_checkout_mutation()
    {
        var item = new PRFactoryWorkItem { Id = Guid.NewGuid(), RepositoryId = Guid.NewGuid() };
        using var h = new ChainHarness(item);
        var secondaryId = Guid.NewGuid();
        h.AddSecondary(secondaryId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PRFactoryRepositorySet.ParseAsync(item,
            [new RepositoryMapping(item.RepositoryId!.Value, h.Repo)], "key"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PRFactoryRepositorySet.ParseAsync(item,
            [new RepositoryMapping(item.RepositoryId!.Value, h.Repo), new RepositoryMapping(secondaryId, h.Repo)], "key"));
        var entries = await PRFactoryRepositorySet.ParseAsync(item,
            [new RepositoryMapping(item.RepositoryId!.Value, h.Repo), new RepositoryMapping(secondaryId, h.SecondaryRepo!)], "key");
        Assert.Equal(2, entries.Length);
    }

    [Fact]
    public async Task Conflict_in_second_repository_restores_first_local_base_refresh()
    {
        var item = new PRFactoryWorkItem
        {
            Id = Guid.NewGuid(),
            Type = "Implementation",
            RepositoryId = Guid.NewGuid(),
            LeaseToken = Guid.NewGuid(),
            AgentType = PRFactoryAgentType.Codex,
            Prompt = "Edit both"
        };
        using var h = new ChainHarness(item);
        h.AddSecondary(Guid.NewGuid());
        await h.TickAsync();
        var key = $"{ChainServer.Url}|{item.Id:D}";
        var sets = new PRFactoryRepositorySet(new PRFactoryRepositorySetStore(h.Database), new PRFactoryWorkspace(h.Workspaces),
            new PRFactoryWorkspaceStore(h.Database), new PRFactoryHandoverStore(h.Database));
        var set = sets.Get(key)!;
        var second = new PRFactoryWorkspaceStore(h.Database).Get(set.Members[1].WorkspaceKey)!;
        var originalPrimary = JobWorktree.Head(new PRFactoryWorkspaceStore(h.Database).Get(key)!.LeadPath);
        var localSecondary = ChainHarness.Commit(second.LeadPath, "base.txt", "local edit");
        ChainHarness.Commit(h.Repo, "later.txt", "moved primary");
        ChainHarness.Git(h.Repo, "push", "origin", "main");
        ChainHarness.Commit(h.SecondaryRepo!, "base.txt", "remote edit");
        ChainHarness.Git(h.SecondaryRepo!, "push", "origin", "main");
        new PRFactoryRepositorySetStore(h.Database).FinishRefresh(key, "restored");
        foreach (var entry in set.Members) { new PRFactoryHandoverStore(h.Database).FinishRefresh(entry.WorkspaceKey, "restored"); }
        var results = await sets.RefreshAsync(set, null);
        Assert.False(results[^1].Result.AgentMayRun);
        Assert.Contains("base.txt", results[^1].Result.ConflictingPaths);
        Assert.Equal(originalPrimary, JobWorktree.Head(new PRFactoryWorkspaceStore(h.Database).Get(key)!.LeadPath));
        Assert.Equal(localSecondary, JobWorktree.Head(second.LeadPath));
    }

    [Fact]
    public async Task Second_push_denied_then_restart_retries_only_unresolved_repository_and_completes_once()
    {
        var item = new PRFactoryWorkItem
        {
            Id = Guid.NewGuid(),
            Type = "Implementation",
            RepositoryId = Guid.NewGuid(),
            LeaseToken = Guid.NewGuid(),
            AgentType = PRFactoryAgentType.Codex,
            Prompt = "Edit both repositories",
            TicketArtefactFolder = "docs/PRF-8"
        };
        using var h = new ChainHarness(item);
        h.AddSecondary(Guid.NewGuid());
        await h.TickAsync();
        var key = $"{ChainServer.Url}|{item.Id:D}";
        var set = new PRFactoryRepositorySetStore(h.Database).Get(key)!;
        Assert.Equal(2, set.Members.Length);
        Assert.True(File.Exists(set.ManifestPath));
        var secondary = new PRFactoryWorkspaceStore(h.Database).Get(set.Members[1].WorkspaceKey)!;
        h.RunQueued(job =>
        {
            ChainHarness.Commit(job.Cwd!, "primary.txt", "primary work");
            ChainHarness.Commit(secondary.LeadPath, "secondary.txt", "secondary work");
        });
        var deny = Path.Combine(h.SecondaryRemote!, "hooks", "pre-receive");
        File.WriteAllText(deny, "#!/bin/sh\nexit 1\n");
        File.SetUnixFileMode(deny, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await h.TickAsync();
        var branch = $"prfactory/{item.Id:D}";
        var firstHead = h.RemoteHead(branch);
        Assert.NotNull(firstHead);
        Assert.Null(ChainHarness.Git(h.SecondaryRepo!, "ls-remote", h.SecondaryRemote!, "refs/heads/" + branch)
            .NullIfEmpty());
        Assert.Empty(h.Server.Completions);
        Assert.Contains(h.Server.RepositoryResults, r => r.GetProperty("repositoryId").GetGuid() == h.SecondaryId
            && r.GetProperty("pushState").GetInt32() == 3);
        File.Delete(deny);
        // Tick constructs fresh connector/store objects over the same database, as a daemon restart does.
        await h.TickAsync();
        Assert.Equal(firstHead, h.RemoteHead(branch));
        Assert.Equal(ChainHarness.Git(secondary.LeadPath, "rev-parse", "HEAD"),
            ChainHarness.Git(h.SecondaryRepo!, "ls-remote", h.SecondaryRemote!, "refs/heads/" + branch).Split('\t')[0]);
        var completion = Assert.Single(h.Server.Completions);
        Assert.Equal(2, completion.GetProperty("repositoryResults").GetArrayLength());
        Assert.Equal("completed", h.Teams.Get(ChainServer.Url, item.Id)!.State);
    }

    [Fact]
    public async Task Changed_remote_invalidates_prior_pushed_result_without_force_rollback()
    {
        var item = new PRFactoryWorkItem
        {
            Id = Guid.NewGuid(),
            Type = "Implementation",
            RepositoryId = Guid.NewGuid(),
            LeaseToken = Guid.NewGuid(),
            AgentType = PRFactoryAgentType.Codex,
            Prompt = "Edit both repositories"
        };
        using var h = new ChainHarness(item);
        h.AddSecondary(Guid.NewGuid());
        await h.TickAsync();
        var set = new PRFactoryRepositorySetStore(h.Database).Get($"{ChainServer.Url}|{item.Id:D}")!;
        var secondary = new PRFactoryWorkspaceStore(h.Database).Get(set.Members[1].WorkspaceKey)!;
        h.RunQueued(job =>
        {
            ChainHarness.Commit(job.Cwd!, "primary.txt", "primary work");
            ChainHarness.Commit(secondary.LeadPath, "secondary.txt", "secondary work");
        });
        var deny = Path.Combine(h.SecondaryRemote!, "hooks", "pre-receive");
        File.WriteAllText(deny, "#!/bin/sh\nexit 1\n");
        File.SetUnixFileMode(deny, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await h.TickAsync();
        var branch = $"prfactory/{item.Id:D}";
        Assert.NotNull(h.RemoteHead(branch));
        File.Delete(deny);
        // Another actor replaces the primary remote branch. ATF must retain it and stop.
        ChainHarness.Git(h.Repo, "push", "--force", "origin", "main:refs/heads/" + branch);
        var changedRemote = h.RemoteHead(branch);
        await h.TickAsync();
        Assert.Equal(changedRemote, h.RemoteHead(branch));
        Assert.Empty(h.Server.Completions);
        Assert.Contains(h.Server.RepositoryResults, r => r.GetProperty("repositoryId").GetGuid() == item.RepositoryId
            && r.GetProperty("pushState").GetInt32() == 3);
    }

    [Fact]
    public async Task Unchanged_secondary_is_skipped_with_reason()
    {
        var item = new PRFactoryWorkItem
        {
            Id = Guid.NewGuid(),
            Type = "Implementation",
            RepositoryId = Guid.NewGuid(),
            LeaseToken = Guid.NewGuid(),
            AgentType = PRFactoryAgentType.Codex,
            Prompt = "Edit primary"
        };
        using var h = new ChainHarness(item);
        h.AddSecondary(Guid.NewGuid());
        await h.TickAsync();
        h.RunQueued(job => ChainHarness.Commit(job.Cwd!, "primary.txt", "changed"));
        await h.TickAsync();
        var outcomes = Assert.Single(h.Server.Completions).GetProperty("repositoryResults");
        var secondary = outcomes.EnumerateArray().Single(r => r.GetProperty("repositoryId").GetGuid() == h.SecondaryId);
        Assert.Equal(1, secondary.GetProperty("pushState").GetInt32());
        Assert.Equal("unchanged repository", secondary.GetProperty("message").GetString());
    }
}

static class MultiRepositoryTestText
{
    public static string? NullIfEmpty(this string value) => value.Length == 0 ? null : value;
}
