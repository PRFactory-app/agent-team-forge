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
    const string SubmitProperties = """
          "backend":{"type":"string","enum":["claude","codex","pi","fake"],"description":"Agent CLI the daemon runs for this job."},
          "instruction":{"type":"string","description":"Task for the agent."},
          "cwd":{"type":"string","description":"Absolute working directory for the agent (optional)."},
          "worktree":{"type":"boolean","description":"Create a private git worktree for this job from cwd's HEAD."},
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

    const string CodexWakeSchema = """
        {"type":"object","properties":{
          "thread_id":{"type":"string","description":"Your CODEX_THREAD_ID from a shell tool."}},
         "required":["thread_id"]}
        """;

    const string FollowUpSchema = """
        {"type":"object","properties":{
          "job_id":{"type":"string","description":"Finished job whose native agent session is resumed."},
          "instruction":{"type":"string"},
          "idempotency_key":{"type":"string","description":"Caller-chosen key; retry with the same key to recover the job."}},
         "required":["job_id","instruction","idempotency_key"]}
        """;

    const string ListSchema = """
        {"type":"object","properties":{
          "status":{"type":"string","enum":["queued","running","completed","failed","needs_reconciliation","cancelled"]},
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

            // Wake is best effort: a missing daemon or credential must not stop the bridge or fail job calls.
            try
            {
                var registration = await client.SendAsync(wakeTarget, cancellationToken);
                if (registration.Ok)
                {
                    wakeGeneration = registration.WakeGeneration;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        await RegisterWakeAsync(CancellationToken.None);
        var tools = new List<Tool>
        {
            new() { Name = "submit_job", Description = "Durably submit a task to an agent (claude, codex or pi) run by the AgentTeamForge daemon. Returns the job; poll get_job for the result.", InputSchema = Parse(testProfile ? TestSubmitSchema : SubmitSchema) },
            new() { Name = "get_job", Description = "Read a job's status, result output and native session_id.", InputSchema = Parse(GetSchema) },
            new() { Name = "stop_job", Description = "Cancel a queued or running job. A finished job is returned unchanged.", InputSchema = Parse(GetSchema) },
            new() { Name = "follow_up", Description = "Send a follow-up instruction into a finished job's native agent session (same backend and cwd). Returns the new job.", InputSchema = Parse(FollowUpSchema) },
            new() { Name = "list_jobs", Description = "List jobs, newest first, one bounded page at a time.", InputSchema = Parse(ListSchema) },
            new() { Name = "register_codex_wake", Description = "Register this Codex conversation for native job notices before submitting jobs. Read CODEX_THREAD_ID with a shell tool and pass it here; Codex does not always pass it to MCP servers.", InputSchema = Parse(CodexWakeSchema) },
            new() { Name = "job_submit", Description = "Durably submit a job to the AgentTeamForge daemon (spike).", InputSchema = Parse(testProfile ? TestSubmitSchema : SubmitSchema) },
            new() { Name = "job_get", Description = "Read a job's committed state and result (spike).", InputSchema = Parse(GetSchema) },
            new() { Name = "job_list", Description = "List your jobs' committed state, newest first, one bounded page at a time (read-only, spike).", InputSchema = Parse(ListSchema) },
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
                    await RegisterWakeAsync(cancellationToken);
                    IpcResponse response;
                    if (call.Name == "register_codex_wake")
                    {
                        var host = HostSessionWake.NearestHost();
                        var home = host?.Kind == "codex" ? HostSessionWake.CodexHome(host.Value.Pid) : null;
                        var target = home is null ? null : HostSessionWake.ForCodexThread(String(args, "thread_id"), home);
                        response = target is null ? new IpcResponse(false, JobErrors.InvalidRequest)
                            : await client.SendAsync(target, cancellationToken);
                        if (response.Ok && response.WakeGeneration is long generation)
                        {
                            wakeTarget = target;
                            wakeGeneration = generation;
                        }
                    }
                    else
                    {
                        var (ipc, rejection) = Map(call.Name, args, testProfile);
                        if (ipc is not null && wakeTarget is not null && wakeGeneration is not null
                            && ipc.Op is IpcProtocol.JobSubmit or IpcProtocol.JobFollowUp or IpcProtocol.JobGet)
                        {
                            ipc = ipc with { WakeKey = wakeTarget.WakeKey, WakeGeneration = wakeGeneration };
                        }
                        response = ipc is null
                            ? new IpcResponse(false, rejection)
                            : await client.SendAsync(ipc, cancellationToken);
                    }
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

    /// <summary>Maps a tool call to one IPC request, or to a rejection code without contacting the daemon.</summary>
    static (IpcRequest? Request, string? Rejection) Map(string name, IDictionary<string, JsonElement> args, bool testProfile) =>
        name switch
        {
            "job_submit" or "submit_job" => (new IpcRequest
            {
                Op = IpcProtocol.JobSubmit,
                IdempotencyKey = String(args, "idempotency_key"),
                Instruction = String(args, "instruction"),
                Backend = String(args, "backend"),
                Cwd = String(args, "cwd"),
                Worktree = args.TryGetValue("worktree", out var worktree) && worktree.ValueKind == JsonValueKind.True,
                Behavior = testProfile ? String(args, "behavior") : null,
                Hold = testProfile && args.TryGetValue("hold", out var hold) && hold.ValueKind == JsonValueKind.True,
            }, null),
            "job_get" or "get_job" => (new IpcRequest { Op = IpcProtocol.JobGet, JobId = String(args, "job_id") }, null),
            "stop_job" => (new IpcRequest { Op = IpcProtocol.JobStop, JobId = String(args, "job_id") }, null),
            "follow_up" => (new IpcRequest
            {
                Op = IpcProtocol.JobFollowUp,
                JobId = String(args, "job_id"),
                Instruction = String(args, "instruction"),
                IdempotencyKey = String(args, "idempotency_key"),
            }, null),
            "job_list" or "list_jobs" => ListRequest(args),
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
