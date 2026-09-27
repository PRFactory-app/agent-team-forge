using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Host.Features.PRFactory;

namespace AgentTeamForge.Tests.PRFactory;

/// <summary>Claim → owned workspace → agent commits → artefacts → verified push → completion, and cancellation.</summary>
public sealed class PublicationChainTests
{
    [Fact]
    public async Task External_members_leaving_cannot_publish_while_the_managed_lead_is_still_running()
    {
        var item = Implementation();
        item.TeamPlan = new PRFactoryTeamPlan
        {
            Members = [new PRFactoryTeamMember { Name = "visitor", Role = "Reviewer" }]
        };
        using var h = new ChainHarness(item) { ExternalMembers = ["visitor"] };
        await h.TickAsync();
        var (lead, run) = h.StartOne();
        ChainHarness.Commit(lead.Cwd!, "partial.txt", "still working");
        var external = h.Teams.External(ChainServer.Url, item.Id, "visitor")!;
        var token = h.External.Join(external.TeamId, external.TicketToken).Member!.MemberToken;
        Assert.True(h.External.Leave(token).Ok);
        await h.TickAsync();
        Assert.Null(h.RemoteHead("prfactory/" + item.Id));
        Assert.Empty(h.Server.Completions);
        Assert.Equal(JobStatus.Running, h.Store.GetJob(lead.JobId)!.Status);
        Assert.True(h.Store.Complete(run, "done"));
        await h.TickAsync();
        Assert.Single(h.Server.Completions);
    }

    static PRFactoryWorkItem Implementation() => new()
    {
        Id = Guid.NewGuid(),
        Type = "Implementation",
        TicketKey = "PRF-42",
        RepositoryId = Guid.NewGuid(),
        LeaseToken = Guid.NewGuid(),
        AgentType = PRFactoryAgentType.Codex,
        Prompt = "Implement the ticket",
        TicketArtefactFolder = "docs/PRF-42",
    };

    [Fact]
    public async Task Writable_implementation_publishes_verified_branch_then_completes_with_its_head()
    {
        using var h = new ChainHarness(Implementation());
        var publish = $"prfactory/{h.Server.Item.Id}";
        await h.TickAsync();
        var lead = Assert.Single(h.RunQueued(job =>
        {
            ChainHarness.Commit(job.Cwd!, "src/feature.txt", "implemented");
            // An uncommitted phase note is an artefact, not code: uploaded, never pushed.
            Directory.CreateDirectory(Path.Combine(job.Cwd!, "docs/PRF-42"));
            File.WriteAllText(Path.Combine(job.Cwd!, "docs/PRF-42/implementation-notes.md"), "# Notes");
        }));
        var head = ChainHarness.Git(lead.Cwd!, "rev-parse", "HEAD");
        Assert.Null(h.RemoteHead(publish));

        await h.TickAsync();
        Assert.Equal(head, h.RemoteHead(publish));
        var receipt = new PRFactoryPublicationStore(h.Database).Get($"{ChainServer.Url}|{h.Server.Item.Id:D}|{h.Server.Item.RepositoryId:D}")!;
        Assert.NotNull(receipt.VerifiedAt);
        Assert.Equal((publish, head, h.BaseSha), (receipt.Intent.PublishBranch, receipt.Intent.HeadSha, receipt.Intent.BaseSha));
        Assert.Contains("implementation-notes.md", Assert.Single(h.Server.Artefacts));
        var completion = Assert.Single(h.Server.Completions);
        Assert.Equal(publish, completion.GetProperty("resultBranch").GetString());
        Assert.Equal(head, completion.GetProperty("resultCommitSha").GetString());
        var publication = completion.GetProperty("publication");
        Assert.True(publication.GetProperty("verified").GetBoolean());
        Assert.Equal(head, publication.GetProperty("headSha").GetString());
        Assert.Equal("completed", h.Teams.Get(ChainServer.Url, h.Server.Item.Id)!.State);
        Assert.Empty(h.Server.Failures);
    }

    [Fact]
    public async Task Dirty_code_fails_visibly_without_pushing_or_committing_for_the_agent()
    {
        using var h = new ChainHarness(Implementation());
        await h.TickAsync();
        var lead = Assert.Single(h.RunQueued(job => File.WriteAllText(Path.Combine(job.Cwd!, "base.txt"), "uncommitted")));
        await h.TickAsync();
        Assert.Null(h.RemoteHead($"prfactory/{h.Server.Item.Id}"));
        Assert.Contains("base.txt", Assert.Single(h.Server.Failures));
        Assert.Empty(h.Server.Completions);
        Assert.Equal("uncommitted", File.ReadAllText(Path.Combine(lead.Cwd!, "base.txt")));
    }

    [Fact]
    public async Task Cancellation_mid_run_stops_the_turn_and_blocks_publication_and_completion()
    {
        using var h = new ChainHarness(Implementation());
        await h.TickAsync();
        var (lead, run) = h.StartOne();
        ChainHarness.Commit(lead.Cwd!, "src/partial.txt", "half done");

        h.Server.Status = 5; // The ticket is cancelled in PRFactory while the agent works.
        await h.TickAsync();
        Assert.Equal(JobStatus.Cancelled, h.Store.GetJob(lead.JobId)!.Status);
        Assert.False(h.Store.Complete(run, "late result")); // A late backend result cannot resurrect it.
        Assert.False(h.Authority.MayLaunch(h.Server.Item.Id));

        await h.TickAsync();
        Assert.Null(h.RemoteHead($"prfactory/{h.Server.Item.Id}"));
        Assert.Empty(h.Server.Artefacts);
        Assert.Empty(h.Server.Completions);
        Assert.Empty(h.Server.Failures);
        var authority = Assert.Single(h.Authorities.Read(ChainServer.Url));
        Assert.Equal(("cancelled", false), (authority.Disposition, authority.Stopping));
        Assert.Equal("failed", h.Teams.Get(ChainServer.Url, h.Server.Item.Id)!.State);
        Assert.Equal("half done", File.ReadAllText(Path.Combine(lead.Cwd!, "src/partial.txt"))); // Local work retained.
    }
}
