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
    const string SubmitProperties = """
          "backend":{"type":"string","enum":["claude","codex","pi","fake"],"description":"Agent CLI the daemon runs for this job."},
          "instruction":{"type":"string","description":"Task for the agent."},
          "cwd":{"type":"string","description":"Absolute working directory for the agent (optional)."},
          "idempotency_key":{"type":"string","description":"Caller-chosen key; retry with the same key to recover the job."}
        """;

    const string SubmitSchema = """{"type":"object","properties":{""" + SubmitProperties + """
        },"required":["backend","instruction","idempotency_key"]}
        """;

    // Test-profile only: fake barrier/behaviour controls. The daemon enforces this independently.
    const string TestSubmitSchema = """{"type":"object","properties":{""" + SubmitProperties + """
          ,"behavior":{"type":"string","enum":["complete","eof_after_ack","exit_after_receipt","mismatched_correlation","hang"]},
          "hold":{"type":"boolean"}},
         "required":["backend","instruction","idempotency_key"]}
        """;

    const string GetSchema = """
        {"type":"object","properties":{"job_id":{"type":"string"}},"required":["job_id"]}
        """;

    const string FollowUpSchema = """
        {"type":"object","properties":{
          "job_id":{"type":"string","description":"Finished job whose native agent session is resumed."},
          "instruction":{"type":"string"},
          "idempotency_key":{"type":"string","description":"Caller-chosen key; retry with the same key to recover the job."}},
         "required":["job_id","instruction","idempotency_key"]}
        """;

    const string ListSchema = """{"type":"object","properties":{}}""";

    public static async Task<int> RunAsync(StateDirectory state, bool testProfile)
    {
        var client = new IpcClient(state, new SpikeLimits());
        var tools = new List<Tool>
        {
            new() { Name = "submit_job", Description = "Durably submit a task to an agent (claude, codex or pi) run by the AgentTeamForge daemon. Returns the job; poll get_job for the result.", InputSchema = Parse(testProfile ? TestSubmitSchema : SubmitSchema) },
            new() { Name = "get_job", Description = "Read a job's status, result output and native session_id.", InputSchema = Parse(GetSchema) },
            new() { Name = "follow_up", Description = "Send a follow-up instruction into a finished job's native agent session (same backend and cwd). Returns the new job.", InputSchema = Parse(FollowUpSchema) },
            new() { Name = "list_jobs", Description = "List recent jobs (without result text).", InputSchema = Parse(ListSchema) },
        };

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "agentteamforge", Version = "0.1.0-mvp" },
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
                        "submit_job" => new IpcRequest
                        {
                            Op = IpcProtocol.JobSubmit,
                            IdempotencyKey = String(args, "idempotency_key"),
                            Instruction = String(args, "instruction"),
                            Backend = String(args, "backend"),
                            Cwd = String(args, "cwd"),
                            Behavior = testProfile ? String(args, "behavior") : null,
                            Hold = testProfile && args.TryGetValue("hold", out var hold) && hold.ValueKind == JsonValueKind.True,
                        },
                        "get_job" => new IpcRequest { Op = IpcProtocol.JobGet, JobId = String(args, "job_id") },
                        "follow_up" => new IpcRequest
                        {
                            Op = IpcProtocol.JobFollowUp,
                            JobId = String(args, "job_id"),
                            Instruction = String(args, "instruction"),
                            IdempotencyKey = String(args, "idempotency_key"),
                        },
                        "list_jobs" => new IpcRequest { Op = IpcProtocol.JobList },
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

        await using var server = McpServer.Create(new StdioServerTransport("agentteamforge"), options);
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
