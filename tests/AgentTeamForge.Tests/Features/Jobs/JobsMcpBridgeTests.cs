using System.Text.Json;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class JobsMcpBridgeTests
{
    [Fact]
    public void Submit_maps_lead_supplied_name_to_target_agent()
    {
        using var json = JsonDocument.Parse("""{"backend":"codex","instruction":"work","idempotency_key":"key","name":"reviewer"}""");
        var args = json.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);

        var (request, rejection) = JobsMcpBridge.Map("submit_job", args, false);

        Assert.Null(rejection);
        Assert.Equal("reviewer", request!.TargetAgent);
    }

    [Fact]
    public void Send_message_to_managed_job_uses_durable_follow_up()
    {
        using var json = JsonDocument.Parse("""{"job_id":"job_123","text":"next","idempotency_key":"next-1"}""");
        var args = json.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);

        var (request, rejection) = JobsMcpBridge.Map("send_message", args, false);

        Assert.Null(rejection);
        Assert.Equal(IpcProtocol.JobFollowUp, request!.Op);
        Assert.Equal("job_123", request.JobId);
        Assert.Equal("next", request.Instruction);
        Assert.Equal("next-1", request.IdempotencyKey);
        Assert.True(request.Defer);
    }

    [Fact]
    public void Send_message_to_external_member_keeps_inbox_route()
    {
        using var json = JsonDocument.Parse("""{"to":"member","text":"note"}""");
        var args = json.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);

        var (request, _) = JobsMcpBridge.Map("send_message", args, false);

        Assert.Equal(IpcProtocol.ExternalLeadSend, request!.Op);
        Assert.Equal("member", request.MemberName);
    }

    [Theory]
    [InlineData("{\"from_agent\":null}", "from_agent")]
    [InlineData("{\"since_seq\":-1}", "since_seq")]
    [InlineData("{\"limit\":\"1\"}", "limit")]
    [InlineData("{\"max_chars\":65537}", "max_chars")]
    [InlineData("{\"full\":null}", "full")]
    public void Read_messages_validation_names_the_bad_field(string jsonText, string field)
    {
        using var json = JsonDocument.Parse(jsonText);
        var args = json.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);

        Assert.Equal(field, JobsMcpBridge.InvalidReadMessagesField(args));
    }
}
