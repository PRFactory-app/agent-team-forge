using System.Text.Json;
using AgentTeamForge.Business;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Transport;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentTeamForge.Host.Features.Jobs;

/// <summary>
/// Thin stdio MCP bridge. Tools are registered through explicit list/call
/// handlers (no reflection-based tool discovery) and forward over private IPC.
/// It owns no job, no database and no dispatch; killing it cannot stop work.
/// </summary>
public static class JobsMcpBridge
{
    const string SubmitSchema = """
        {"type":"object","properties":{
          "idempotency_key":{"type":"string","description":"Caller-chosen key; retry with the same key to recover the job."},
          "instruction":{"type":"string"}},
         "required":["idempotency_key","instruction"]}
        """;

    // Test-profile only: fake barrier/behaviour controls. The daemon enforces this independently.
    const string TestSubmitSchema = """
        {"type":"object","properties":{
          "idempotency_key":{"type":"string","description":"Caller-chosen key; retry with the same key to recover the job."},
          "instruction":{"type":"string"},
          "behavior":{"type":"string","enum":["complete","eof_after_ack","exit_after_receipt","mismatched_correlation","hang"]},
          "hold":{"type":"boolean"}},
         "required":["idempotency_key","instruction"]}
        """;

    const string GetSchema = """
        {"type":"object","properties":{"job_id":{"type":"string"}},"required":["job_id"]}
        """;

    public static async Task<int> RunAsync(StateDirectory state, bool testProfile)
    {
        var client = new IpcClient(state, new SpikeLimits());
        var tools = new List<Tool>
        {
            new() { Name = "job_submit", Description = "Durably submit a job to the AgentTeamForge daemon (spike).", InputSchema = Parse(testProfile ? TestSubmitSchema : SubmitSchema) },
            new() { Name = "job_get", Description = "Read a job's committed state and result (spike).", InputSchema = Parse(GetSchema) },
        };

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "agentteamforge-spike", Version = "0.0.1-spike" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = tools }),
                CallToolHandler = async (request, cancellationToken) =>
                {
                    var call = request.Params ?? throw new InvalidOperationException("missing params");
                    var args = call.Arguments ?? new Dictionary<string, JsonElement>();
                    var ipc = call.Name switch
                    {
                        "job_submit" => new IpcRequest
                        {
                            Op = IpcProtocol.JobSubmit,
                            IdempotencyKey = String(args, "idempotency_key"),
                            Instruction = String(args, "instruction"),
                            Behavior = testProfile ? String(args, "behavior") : null,
                            Hold = testProfile && args.TryGetValue("hold", out var hold) && hold.ValueKind == JsonValueKind.True,
                        },
                        "job_get" => new IpcRequest { Op = IpcProtocol.JobGet, JobId = String(args, "job_id") },
                        _ => null,
                    };
                    var response = ipc is null
                        ? new IpcResponse(false, IpcProtocol.UnknownOp)
                        : await client.SendAsync(ipc, cancellationToken);
                    return new CallToolResult
                    {
                        IsError = !response.Ok,
                        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(response, IpcJson.Default.IpcResponse) }],
                    };
                },
            },
        };

        await using var server = McpServer.Create(new StdioServerTransport("agentteamforge-spike"), options);
        await server.RunAsync();
        return 0;
    }

    static string? String(IDictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
