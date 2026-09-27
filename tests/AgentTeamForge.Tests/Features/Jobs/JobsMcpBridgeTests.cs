using System.Text.Json;
using AgentTeamForge.Host.Features.Jobs;

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
}
