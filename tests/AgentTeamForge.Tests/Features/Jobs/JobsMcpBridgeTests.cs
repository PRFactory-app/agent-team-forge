using System.Text.Json;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class JobsMcpBridgeTests
{
    [Theory]
    [InlineData("planner")]
    [InlineData("")]
    public void Set_session_name_maps_to_session_info(string name)
    {
        using var json = JsonDocument.Parse(name.Length == 0 ? """{"name":""}""" : """{"name":"planner"}""");
        var args = json.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        var (request, rejection) = JobsMcpBridge.Map("set_session_name", args, false);
        Assert.Null(rejection);
        Assert.Equal(IpcProtocol.SessionInfo, request!.Op);
        Assert.Equal(name, request.SessionName);
    }

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
    [InlineData("{\"from_agent\":7}", "from_agent")]
    [InlineData("{\"since_seq\":-1}", "since_seq")]
    [InlineData("{\"limit\":\"1\"}", "limit")]
    [InlineData("{\"max_chars\":65537}", "max_chars")]
    [InlineData("{\"full\":\"true\"}", "full")]
    public void Read_messages_validation_names_the_bad_field(string jsonText, string field)
    {
        using var json = JsonDocument.Parse(jsonText);
        var args = json.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);

        Assert.Equal(field, JobsMcpBridge.InvalidReadMessagesField(args));
    }

    [Fact]
    public void Read_messages_treats_null_and_empty_optional_arguments_as_absent()
    {
        using var json = JsonDocument.Parse("""{"from_agent":"","since_seq":null,"limit":null,"max_chars":null,"full":null}""");
        var args = JobsMcpBridge.WithoutNulls(json.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value));

        Assert.Null(JobsMcpBridge.InvalidReadMessagesField(args));
        var (request, _) = JobsMcpBridge.Map("read_messages", args, false);
        Assert.Equal(((long?)null, (int?)null, (int?)null, false), (request!.SinceSeq, request.Limit, request.MaxChars, request.Full));
    }
}
