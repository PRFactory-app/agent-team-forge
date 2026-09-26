using System.Text.Json;
using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Features.Wake;
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

    const string ListSchema = """
        {"type":"object","properties":{
          "status":{"type":"string","enum":["queued","running","completed","failed","needs_reconciliation"]},
          "limit":{"type":"integer","minimum":1,"maximum":50,"description":"Page size; default 20."},
          "cursor":{"type":"string","description":"next_cursor from the previous page."}}}
        """;

    public static async Task<int> RunAsync(StateDirectory state, bool testProfile)
    {
        var client = new IpcClient(state, new SpikeLimits());
        var wakeTarget = HostSessionWake.Resolve(state);
        long? wakeGeneration = null;
        async Task RegisterWakeAsync(CancellationToken cancellationToken)
        {
            if (wakeTarget is null || wakeGeneration is not null)
            {
                return;
            }

            var registration = await client.SendAsync(wakeTarget, cancellationToken);
            if (registration.Ok)
            {
                wakeGeneration = registration.WakeGeneration;
            }
        }
        await RegisterWakeAsync(CancellationToken.None);
        var tools = new List<Tool>
        {
            new() { Name = "job_submit", Description = "Durably submit a job to the AgentTeamForge daemon (spike).", InputSchema = Parse(testProfile ? TestSubmitSchema : SubmitSchema) },
            new() { Name = "job_get", Description = "Read a job's committed state and result (spike).", InputSchema = Parse(GetSchema) },
            new() { Name = "job_list", Description = "List your jobs' committed state, newest first, one bounded page at a time (read-only, spike).", InputSchema = Parse(ListSchema) },
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
                    await RegisterWakeAsync(cancellationToken);
                    var (ipc, rejection) = Map(call.Name, args, testProfile);
                    if (ipc is not null && wakeTarget is not null && wakeGeneration is not null
                        && ipc.Op is IpcProtocol.JobSubmit or IpcProtocol.JobGet)
                    {
                        ipc = ipc with { WakeKey = wakeTarget.WakeKey, WakeGeneration = wakeGeneration };
                    }
                    var response = ipc is null
                        ? new IpcResponse(false, rejection)
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

    /// <summary>Maps a tool call to one IPC request, or to a rejection code without contacting the daemon.</summary>
    static (IpcRequest? Request, string? Rejection) Map(string name, IDictionary<string, JsonElement> args, bool testProfile) =>
        name switch
        {
            "job_submit" => (new IpcRequest
            {
                Op = IpcProtocol.JobSubmit,
                IdempotencyKey = String(args, "idempotency_key"),
                Instruction = String(args, "instruction"),
                Behavior = testProfile ? String(args, "behavior") : null,
                Hold = testProfile && args.TryGetValue("hold", out var hold) && hold.ValueKind == JsonValueKind.True,
            }, null),
            "job_get" => (new IpcRequest { Op = IpcProtocol.JobGet, JobId = String(args, "job_id") }, null),
            "job_list" => ListRequest(args),
            _ => (null, IpcProtocol.UnknownOp),
        };

    /// <summary>
    /// Present optional fields must match the advertised schema type. A null or wrongly typed
    /// status/cursor is rejected here instead of being read as absent (no filter / first page).
    /// </summary>
    static (IpcRequest?, string?) ListRequest(IDictionary<string, JsonElement> args) =>
        OptionalString(args, "status", out var status) && OptionalString(args, "cursor", out var cursor)
            ? (new IpcRequest { Op = IpcProtocol.JobList, Status = status, Limit = Integer(args, "limit"), Cursor = cursor }, null)
            : (null, JobErrors.InvalidRequest);

    static bool OptionalString(IDictionary<string, JsonElement> args, string name, out string? value)
    {
        value = null;
        if (!args.TryGetValue(name, out var element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }

    static string? String(IDictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A present but non-integer (or null) value becomes 0 so the daemon rejects it rather than defaulting.</summary>
    static int? Integer(IDictionary<string, JsonElement> args, string name) =>
        !args.TryGetValue(name, out var value) ? null
        : value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;

    static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
