using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Host.Features.PRFactory;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class HumanWaitChainTests
{
    [Fact]
    public async Task Question_ends_turn_answer_command_resumes_same_session_then_publishes_and_completes()
    {
        using var h = new ChainHarness(new PRFactoryWorkItem
        {
            Id = Guid.NewGuid(),
            Type = "Implementation",
            TicketKey = "PRF-7",
            RepositoryId = Guid.NewGuid(),
            LeaseToken = Guid.NewGuid(),
            AgentType = PRFactoryAgentType.Codex,
            Prompt = "Implement",
        });
        await h.TickAsync();
        var (lead, run) = h.StartOne();
        Assert.Contains("request_human_input", lead.Instruction, StringComparison.Ordinal);

        // The agent calls request_human_input (authenticated as its managed-child membership), then ends its turn.
        var asked = PRFactoryInteraction.RequestFromManagedChild(h.HumanWait, h.Store, h.Authorities,
            "child-" + lead.JobId, "Which database should I use?", "db-choice");
        Assert.Null(asked.Error);
        Assert.Equal(asked.Wait!.QuestionId, PRFactoryInteraction.RequestFromManagedChild(h.HumanWait, h.Store, h.Authorities,
            "child-" + lead.JobId, "Which database should I use?", "db-choice").Wait!.QuestionId);
        Assert.Equal(JobErrors.NotFound, PRFactoryInteraction.RequestFromManagedChild(h.HumanWait, h.Store, h.Authorities,
            "child-" + Guid.NewGuid().ToString("N"), "spoofed", "k").Error);
        Assert.True(h.Store.Complete(run, "Asked the owner; ending turn."));

        await h.TickAsync();
        Assert.Empty(h.Server.Completions); // An open question holds completion.
        Assert.Contains(h.Server.Lines, l => l.RecordKind == "human-wait" && l.Text.Contains("Which database", StringComparison.Ordinal)
            && l.Text.Contains("\"waiting\"", StringComparison.Ordinal));

        var command = Guid.NewGuid();
        h.Server.Commands.Add(new(command, "SendMessage", "lead", "Use Postgres", asked.Wait.QuestionId));
        await h.TickAsync();
        Assert.True(Assert.Single(h.Server.Acks).GetProperty("accepted").GetBoolean());
        var resumed = Assert.Single(h.RunQueued(job =>
        {
            Assert.Equal(lead.JobId, job.ParentJobId); // Same saved session, same owned checkout.
            Assert.Equal(lead.Cwd, job.Cwd);
            Assert.Contains("Human answer: Use Postgres", job.Instruction, StringComparison.Ordinal);
            ChainHarness.Commit(job.Cwd!, "db.txt", "postgres");
        }));
        Assert.Equal("session-" + lead.JobId, h.Store.GetJob(lead.JobId)!.SessionId); // The session the follow-up resumes.

        h.Server.Commands.Add(new(command, "SendMessage", "lead", "Use Postgres", asked.Wait.QuestionId)); // Redelivered.
        await h.TickAsync();
        Assert.Equal("applied", h.HumanWaits.Get(asked.Wait.QuestionId)!.Status);
        Assert.Equal(2, h.Teams.MemberJobs(ChainServer.Url, h.Server.Item.Id).Count); // One answer turn only.
        var completion = Assert.Single(h.Server.Completions);
        Assert.Equal(ChainHarness.Git(lead.Cwd!, "rev-parse", "HEAD"), completion.GetProperty("resultCommitSha").GetString());
        Assert.Equal(completion.GetProperty("resultCommitSha").GetString(), h.RemoteHead("prfactory/" + h.Server.Item.Id));
        Assert.Contains(h.Server.Lines, l => l.RecordKind == "human-wait" && l.Text.Contains("\"applied\"", StringComparison.Ordinal));
    }
}
