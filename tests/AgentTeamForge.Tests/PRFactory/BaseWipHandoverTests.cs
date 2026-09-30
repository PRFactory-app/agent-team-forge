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
        Assert.False(Directory.Exists(workspace.LeadPath));
        Assert.Equal(head, ChainHarness.Git(h.Repo, "rev-parse", "refs/heads/" + workspace.InternalBranch));
    }

    [Fact]
    public async Task Request_holds_release_while_a_child_has_commits_outside_the_lead()
    {
        var item = new PRFactoryWorkItem
        {
            Id = Guid.NewGuid(),
            TicketKey = "PRF-42",
            Type = "Implementation",
            RepositoryId = Guid.NewGuid(),
            LeaseToken = Guid.NewGuid(),
            AgentType = PRFactoryAgentType.Codex,
            Prompt = "Work",
            TeamPlan = new PRFactoryTeamPlan
            {
                MaxConcurrentChildren = 1,
                Members = [new PRFactoryTeamMember { Name = "worker", Role = "Implementer", Order = 1 }]
            }
        };
        using var h = new ChainHarness(item);
        h.Server.BaseWipSupported = true;
        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        h.RunQueued(_ => { });
        var workspace = h.Workspaces.Get($"{ChainServer.Url}|{item.Id:D}")!;
        var child = ChainHarness.Commit(Assert.Single(workspace.Members).Path, "child.txt", "not integrated");
        h.Server.HandoverRequested = true;

        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        Assert.Empty(h.Server.Releases);
        Assert.Equal("claimed", h.Teams.Get(ChainServer.Url, item.Id)!.State);
        Assert.Equal(child, ChainHarness.Git(workspace.Members[0].Path, "rev-parse", "HEAD"));
        Assert.Contains(h.Logs, line => line.Contains("child commits", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cleanup_keeps_worktrees_when_the_lead_moved_past_the_released_receipt()
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
        var key = $"{ChainServer.Url}|{item.Id:D}";
        var workspace = h.Workspaces.Get(key)!;
        var released = ChainHarness.Commit(workspace.LeadPath, "work.txt", "released");
        const string branch = "wip/source/PRF-42";
        ChainHarness.Git(workspace.LeadPath, "push", "origin", released + ":refs/heads/" + branch);
        var store = new PRFactoryHandoverStore(h.Database);
        store.SaveWip(new WipRecord(key, branch, released, null, "reported", "receipt-1"));
        store.RecordRelease(key, new WipReleaseRecord("release-1", released, DateTimeOffset.UtcNow));
        var later = ChainHarness.Commit(workspace.LeadPath, "later.txt", "after release");

        var handover = new PRFactoryHandover(h.Server.Client(), store, (_, _, _) => Task.FromResult(false));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handover.CleanupReleasedAsync(workspace, () => true, TimeSpan.Zero));
        Assert.Equal(later, ChainHarness.Git(workspace.LeadPath, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Released_workspace_with_ignored_env_is_retained()
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
        var key = $"{ChainServer.Url}|{item.Id:D}";
        var workspace = h.Workspaces.Get(key)!;
        ChainHarness.Commit(workspace.LeadPath, ".gitignore", ".env\n");
        var released = ChainHarness.Commit(workspace.LeadPath, "work.txt", "released");
        File.WriteAllText(Path.Combine(workspace.LeadPath, ".env"), "SECRET=1");
        const string branch = "wip/source/PRF-42";
        ChainHarness.Git(workspace.LeadPath, "push", "origin", released + ":refs/heads/" + branch);
        var store = new PRFactoryHandoverStore(h.Database);
        store.SaveWip(new WipRecord(key, branch, released, null, "reported", "receipt-1"));
        store.RecordRelease(key, new WipReleaseRecord("release-1", released, DateTimeOffset.UtcNow));

        var handover = new PRFactoryHandover(h.Server.Client(), store, (_, _, _) => Task.FromResult(false));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handover.CleanupReleasedAsync(workspace, () => true, TimeSpan.Zero));
        Assert.Equal("SECRET=1", File.ReadAllText(Path.Combine(workspace.LeadPath, ".env")));

        h.Teams.Finish(ChainServer.Url, item.Id, "completed");
        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        Assert.Single(h.Logs, line => line.Contains("cleanup refused:", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Logs, line => line.Contains("deferred (InvalidOperationException)", StringComparison.Ordinal));
    }

    static PRFactoryWorkItem Work(string type) => new()
    {
        Id = Guid.NewGuid(),
        TicketKey = "PRF-42",
        Type = type,
        RepositoryId = Guid.NewGuid(),
        LeaseToken = Guid.NewGuid(),
        AgentType = PRFactoryAgentType.Codex,
        Prompt = "Work",
        TicketArtefactFolder = "docs/PRF-42",
    };

    static void WriteNote(string cwd)
    {
        Directory.CreateDirectory(Path.Combine(cwd, "docs/PRF-42"));
        File.WriteAllText(Path.Combine(cwd, "docs/PRF-42/qa.md"), "# Notes");
    }

    [Fact]
    public async Task Refinement_completes_without_any_wip_publication()
    {
        using var h = new ChainHarness(Work("TicketRefinement"));
        h.Server.BaseWipSupported = true;
        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        h.RunQueued(job => WriteNote(job.Cwd!));
        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        Assert.Empty(h.Server.WipReports);
        Assert.Single(h.Server.Completions);
        Assert.Empty(h.Server.Failures);
    }

    [Fact]
    public async Task Rejected_wip_report_is_sent_once_per_head_and_does_not_block_completion()
    {
        using var h = new ChainHarness(Work("Implementation"));
        h.Server.BaseWipSupported = true;
        h.Server.WipRejection = System.Net.HttpStatusCode.BadRequest;
        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        var (lead, run) = h.StartOne();
        ChainHarness.Commit(lead.Cwd!, "feature.txt", "work");
        for (var i = 0; i < 3; i++)
        {
            await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        }
        Assert.Equal(2, h.Server.WipReports.Count); // the base head, then the committed head: one each
        Assert.Contains(h.Logs, l => l.Contains("rejected (400", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Logs, l => l.Contains("deferred (HttpRequestException", StringComparison.Ordinal));
        Assert.True(h.Store.Complete(run, "done"));
        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        Assert.Equal(2, h.Server.WipReports.Count);
        Assert.Single(h.Server.Completions);
    }

    [Fact]
    public async Task Lead_instruction_states_the_primary_path_and_the_job_is_named_after_the_work()
    {
        using var h = new ChainHarness(Work("TicketRefinement"));
        await h.Adapter().TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        var (lead, _) = h.StartOne();
        Assert.Contains("PRFACTORY_PRIMARY_REPO_PATH=" + lead.Cwd, lead.Instruction, StringComparison.Ordinal);
        Assert.Equal("PRF-42_refinement_lead", lead.TargetAgent);
    }

    [Fact]
    public async Task Refinement_with_a_pending_handover_request_still_completes_once_without_a_release()
    {
        using var h = new ChainHarness(Work("TicketRefinement"));
        h.Server.BaseWipSupported = true;
        h.Server.HandoverRequested = true;
        await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        h.RunQueued(job => WriteNote(job.Cwd!));
        for (var i = 0; i < 3; i++)
        {
            await h.Adapter(baseWip: true).TickAsync(ChainHarness.Machine, TestContext.Current.CancellationToken);
        }
        Assert.Empty(h.Server.WipReports);
        Assert.Empty(h.Server.Releases);
        Assert.Single(h.Server.Completions);
        Assert.Empty(h.Server.Failures);
    }

    [Theory]
    [InlineData("-ABC-1")]
    [InlineData("_ABC-1")]
    [InlineData("A.B-1")]
    [InlineData("")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ-ABCDEFGHIJKLMNOPQRSTUVWXYZ-ABCDEFGHIJKLMNOPQRSTUVWXYZ-1")]
    public void Connector_job_names_are_valid_and_unique_per_member(string key)
    {
        var item = Work("TicketRefinement");
        item.TicketKey = key;
        var lead = PRFactoryWorkItems.JobName(item, "lead");
        var member = PRFactoryWorkItems.JobName(item, "reviewer");
        Assert.True(AgentTeamForge.Business.Features.Jobs.AcceptJob.ValidAgentName(lead), lead);
        Assert.True(AgentTeamForge.Business.Features.Jobs.AcceptJob.ValidAgentName(member), member);
        Assert.NotEqual(lead, member);
        Assert.EndsWith("_refinement_lead", lead);
    }

    [Fact]
    public void Connector_job_names_keep_distinct_keys_distinct_and_clean_keys_readable()
    {
        var a = Work("TicketRefinement"); a.TicketKey = "A.B-1";
        var b = Work("TicketRefinement"); b.TicketKey = "AB-1";
        Assert.NotEqual(PRFactoryWorkItems.JobName(a, "lead"), PRFactoryWorkItems.JobName(b, "lead"));
        Assert.Equal("AB-1_refinement_lead", PRFactoryWorkItems.JobName(b, "lead"));
    }
}
