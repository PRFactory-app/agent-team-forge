using System.Net;
using System.Text;
using System.Text.Json;
using AgentTeamForge.Host.Features.PRFactory;

namespace AgentTeamForge.Tests.PRFactory;

/// <summary>Minimal PRFactory worker API: one claimable item, durable acceptance, artefacts and completion.</summary>
sealed class ChainServer(PRFactoryWorkItem item)
{
    public const string Url = "https://example.test";
    public PRFactoryWorkItem Item { get; } = item;
    public int Status { get; set; } = 1;
    public string? AcceptedJobId { get; private set; }
    public List<string> Artefacts { get; } = [];
    public List<JsonElement> Completions { get; } = [];
    public List<string> Failures { get; } = [];
    public bool Offered { get; set; } = true;
    public List<PRFactoryCommand> Commands { get; } = [];
    public List<JsonElement> Acks { get; } = [];
    public List<PRFactoryStreamLine> Lines { get; } = [];

    public PRFactoryClient Client() =>
        new(PRFactoryClient.CreateHttpClient(Url, "fake-token", new Handler(Reply)));

    HttpResponseMessage Reply(HttpRequestMessage request)
    {
        lock (this)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/agent-commands", StringComparison.Ordinal))
            {
                return Json(JsonSerializer.Serialize(new PRFactoryCommandDrainResponse([.. Commands]), PRFactoryWorkItemJson.Default.PRFactoryCommandDrainResponse));
            }
            if (path.EndsWith("/agent-stream", StringComparison.Ordinal))
            {
                var stream = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Lines.AddRange(JsonSerializer.Deserialize(stream, PRFactoryWorkItemJson.Default.PRFactoryStreamBatch)!.Lines);
                request.Content = new StringContent(stream);
                return ManagedWire.Reply(request)!;
            }
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            if (path.EndsWith("/agent-commands/ack", StringComparison.Ordinal))
            {
                foreach (var ack in JsonElement.Parse(body!).GetProperty("acks").EnumerateArray())
                {
                    Acks.Add(ack);
                    Commands.RemoveAll(c => c.CommandId == ack.GetProperty("commandId").GetGuid());
                }
                return Json("{\"applied\":1}");
            }
            if (path.EndsWith("/poll", StringComparison.Ordinal))
            {
                return Json(Offered ? "{\"workItems\":[" + ItemJson() + "]}" : "{\"workItems\":[]}");
            }
            if (path.Contains("/claim/", StringComparison.Ordinal))
            {
                Offered = false;
                return Json("{\"workItem\":" + ItemJson() + "}");
            }
            if (path.EndsWith("/atf-acceptance", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Post)
                {
                    AcceptedJobId ??= JsonElement.Parse(body!).GetProperty("jobId").GetString();
                }
                return AcceptedJobId is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : Json("{\"atfJobId\":\"" + AcceptedJobId + "\",\"status\":" + Status + "}");
            }
            if (Status is 5 or 6 && (path.Contains("/artefacts/", StringComparison.Ordinal)
                || path.Contains("/complete/", StringComparison.Ordinal) || path.Contains("/fail/", StringComparison.Ordinal)))
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict); // Stale authority is rejected server-side too.
            }
            if (path.Contains("/artefacts/", StringComparison.Ordinal))
            {
                Artefacts.Add(body!);
                return Json("{\"accepted\":true}");
            }
            if (path.Contains("/complete/", StringComparison.Ordinal))
            {
                Completions.Add(JsonElement.Parse(body!));
                Status = 3;
                return Json("{\"accepted\":true}");
            }
            if (path.Contains("/fail/", StringComparison.Ordinal))
            {
                Failures.Add(body!);
                Status = 4;
                return Json("{\"acknowledged\":true}");
            }
            throw new InvalidOperationException(path);
        }
    }

    string ItemJson() => JsonSerializer.Serialize(Item, PRFactoryWorkItemJson.Default.PRFactoryWorkItem);

    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(reply(request));
    }
}
