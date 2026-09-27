using System.Net;
using System.Text;
using System.Text.Json;
using AgentTeamForge.Host.Features.PRFactory;

namespace AgentTeamForge.Tests.PRFactory;

static class ManagedWire
{
    public static HttpResponseMessage? Reply(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/api/work-item-blobs/capabilities") { return new(HttpStatusCode.NotFound); }
        if (path.EndsWith("/agent-commands", StringComparison.Ordinal))
        {
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"commands\":[]}", Encoding.UTF8, "application/json") };
        }
        if (!path.EndsWith("/agent-stream", StringComparison.Ordinal)) { return null; }
        var batch = JsonSerializer.Deserialize(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(), PRFactoryWorkItemJson.Default.PRFactoryStreamBatch)!;
        var through = batch.Lines.GroupBy(l => l.AgentName).ToDictionary(g => g.Key, g => g.Max(l => l.Seq));
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new PRFactoryStreamResponse(true, through, 100, 100000), PRFactoryWorkItemJson.Default.PRFactoryStreamResponse), Encoding.UTF8, "application/json")
        };
    }
}
