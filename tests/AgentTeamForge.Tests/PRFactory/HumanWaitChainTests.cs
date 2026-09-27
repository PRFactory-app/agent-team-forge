using System.Net;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Features.PRFactory;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class HumanWaitChainTests
{
    [Theory]
    [InlineData("ending_turn", "Waiting")]
    [InlineData("waiting", "Waiting")]
    [InlineData("answer_reserved", "Waiting")]
    [InlineData("resumed", "Running")]
    [InlineData("applied", "Running")]
    [InlineData("failed", "Failed")]
    [InlineData("cancelled", "Failed")]
    public void Human_wait_wire_state_is_in_prfactory_lifecycle_enum(string internalStatus, string expected)
    {
        var accepted = new HashSet<string>(["Starting", "Running", "Waiting", "Idle", "Done", "Killed", "Failed", "Parked"]);
        var state = PRFactoryWorkItems.HumanWaitLifecycle(internalStatus);
        Assert.Equal(expected, state);
        Assert.Contains(state, accepted);
    }

    [Theory]
    [InlineData("WaitingForHuman", "Waiting")]
    [InlineData("AnswerQueued", "Waiting")]
    [InlineData("AnswerApplied", "Running")]
    [InlineData("Failed", "Failed")]
    public void Frozen_legacy_notice_replays_with_prfactory_lifecycle(string frozen, string expected)
    {
        var batch = new PRFactoryStreamBatch(Guid.NewGuid(), "human:q:waiting",
            [new("member", "team", "lead", "codex", frozen, null, null)], []);
        Assert.Equal(expected, PRFactoryWorkItems.WithoutLegacyLifecycle(batch).Events.Single().State);
    }

    [Fact]
    public async Task Prfactory_question_tool_is_gated_and_direct_call_does_not_create_a_wait()
    {
        using var h = NewHarness();
        await h.TickAsync();
        var (lead, _) = h.StartOne();
        Assert.DoesNotContain("request_human_input", lead.Instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("human-wait-v1", PRFactoryClient.Capabilities);
        Assert.False(JobsMcpBridge.ShouldOfferHumanInput("child-token", false));
        Assert.True(JobsMcpBridge.ShouldOfferHumanInput("child-token", true));

        var asked = PRFactoryInteraction.RequestFromManagedChild("child-" + lead.JobId);
        Assert.Equal(PRFactoryInteraction.HumanInputUnavailable, asked.Error);
        Assert.Contains("best judgement", asked.Error, StringComparison.Ordinal);
        Assert.Contains("artefact", asked.Error, StringComparison.Ordinal);
        Assert.Empty(h.HumanWaits.ForTeam(ChainServer.Url, h.Server.Item.Id));
    }

    [Fact]
    public async Task Agent_stream_bad_request_reports_failure_once_instead_of_retrying_forever()
    {
        using var h = NewHarness();
        h.Server.StreamRejection = HttpStatusCode.BadRequest;
        await h.TickAsync();
        h.RunQueued(_ => { });
        await h.TickAsync();

        Assert.Single(h.Server.Failures);
        Assert.Contains("HTTP 400", h.Server.Failures[0], StringComparison.Ordinal);
        Assert.Contains(h.Logs, log => log.Contains("agent-stream rejected (400)", StringComparison.Ordinal));
        var posts = h.Server.StreamPosts;
        await h.TickAsync();
        Assert.Equal(posts, h.Server.StreamPosts);
        Assert.Single(h.Server.Failures);
    }

    static ChainHarness NewHarness() => new(new PRFactoryWorkItem
    {
        Id = Guid.NewGuid(),
        Type = "Implementation",
        TicketKey = "PRF-7",
        RepositoryId = Guid.NewGuid(),
        LeaseToken = Guid.NewGuid(),
        AgentType = PRFactoryAgentType.Codex,
        Prompt = "Implement",
        ReadOnly = true,
    });
}
