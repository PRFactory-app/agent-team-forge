using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Scenarios;

[Trait("Category", "Scenario")]
public sealed class ExternalJoinScenarios
{
    [Fact]
    public async Task Separate_member_bridge_joins_and_exchanges_messages_with_lead()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        await rig.StartDaemonAsync();
        var (_, lead) = await rig.StartBridgeAsync("external-lead");
        var (_, member) = await rig.StartBridgeAsync(externalOnly: true);
        var tools = await member.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, tool => tool.Name == "join_team");
        Assert.DoesNotContain(tools, tool => tool.Name == "create_join_ticket");

        var ticket = (await SpikeRig.CallAsync(lead, "create_join_ticket", new() { ["name"] = "visitor" })).Ticket!;
        var joined = (await SpikeRig.CallAsync(member, "join_team", new()
        {
            ["session_id"] = ticket.SessionId,
            ["token"] = ticket.Token
        })).Member!;
        Assert.Equal("invalid_or_expired_ticket", (await SpikeRig.CallAsync(member, "join_team", new()
        {
            ["session_id"] = ticket.SessionId,
            ["token"] = ticket.Token
        })).Error);
        Assert.True((await SpikeRig.CallAsync(lead, "send_message", new() { ["to"] = joined.Name, ["text"] = "work" })).Ok);
        Assert.Equal("work", Assert.Single((await SpikeRig.CallAsync(member, "external_read", new()
        {
            ["member_token"] = joined.MemberToken
        })).Inbox!.Messages).Text);
        Assert.True((await SpikeRig.CallAsync(member, "external_send", new()
        {
            ["member_token"] = joined.MemberToken,
            ["text"] = "done"
        })).Ok);
        Assert.Equal("done", Assert.Single((await SpikeRig.CallAsync(lead, "read_messages", [])).Inbox!.Messages).Text);
        Assert.True((await SpikeRig.CallAsync(member, "leave_team", new() { ["member_token"] = joined.MemberToken })).Ok);
        Assert.Equal("membership_revoked", (await SpikeRig.CallAsync(member, "external_read", new()
        {
            ["member_token"] = joined.MemberToken
        })).Error);
    }
}
