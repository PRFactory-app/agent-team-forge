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
    const string LimitProperties = """
          "timeout_s":{"type":"integer","minimum":1,"maximum":86400,"description":"Cancel the job (reason timeout) this many seconds after it starts running."},
          "queue_ttl_s":{"type":"integer","minimum":1,"maximum":86400,"description":"Cancel the job (reason queue_ttl) if it has not started this many seconds after acceptance."}
        """;

    const string SubmitProperties = """
          "backend":{"type":"string","enum":["claude","codex","pi","fake"],"description":"Agent CLI the daemon runs for this job."},
          "instruction":{"type":"string","description":"Task for the agent."},
          "cwd":{"type":"string","description":"Absolute working directory for the agent (optional)."},
          "worktree":{"type":"boolean","description":"Create a private git worktree for this job from cwd's HEAD."},
          "idempotency_key":{"type":"string","description":"Caller-chosen key; retry with the same key to recover the job."},
        """ + LimitProperties;

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

    const string OutputSchema = """
        {"type":"object","properties":{
          "job_id":{"type":"string"},
          "offset":{"type":"integer","minimum":0,"description":"Absolute byte offset; default 0."},
          "max_bytes":{"type":"integer","minimum":1,"maximum":65536,"description":"Maximum bytes; default 65536."}},
         "required":["job_id"]}
        """;

    const string FollowUpSchema = """
        {"type":"object","properties":{
          "job_id":{"type":"string","description":"Job whose native agent session is resumed."},
          "instruction":{"type":"string"},
          "idempotency_key":{"type":"string","description":"Caller-chosen key; retry with the same key to recover the job."},
          "interrupt":{"type":"boolean","description":"If the parent is running, cancel its turn (reason interrupted) and run this prompt in the same session."},
        """ + LimitProperties + """
        },"required":["job_id","instruction","idempotency_key"]}
        """;

    const string ListSchema = """
        {"type":"object","properties":{
          "status":{"type":"string","enum":["queued","running","completed","failed","needs_reconciliation","cancelled"]},
          "backend":{"type":"string","enum":["fake","claude","codex","pi"]},
          "since":{"type":"string","description":"Include jobs accepted at or after this ISO 8601 time."},
          "limit":{"type":"integer","minimum":1,"maximum":50,"description":"Page size; default 20."},
          "cursor":{"type":"string","description":"next_cursor from the previous page."},
          "all_workspace":{"type":"boolean","description":"Include every lead's jobs in this workspace."}}}
        """;

    const string ResumeSchema = """{"type":"object","properties":{"session_id":{"type":"string"}},"required":["session_id"]}""";
    const string EmptySchema = """{"type":"object","properties":{}}""";
    const string TicketSchema = """{"type":"object","properties":{"name":{"type":"string"},"note":{"type":"string"}},"required":["name"]}""";
    const string JoinSchema = """{"type":"object","properties":{"session_id":{"type":"string"},"token":{"type":"string"}},"required":["session_id","token"]}""";
    const string MemberSendSchema = """{"type":"object","properties":{"member_token":{"type":"string"},"text":{"type":"string"}},"required":["member_token","text"]}""";
    const string LeadSendSchema = """{"type":"object","properties":{"to":{"type":"string"},"text":{"type":"string"}},"required":["to","text"]}""";
    const string MemberReadSchema = """{"type":"object","properties":{"member_token":{"type":"string"},"from_agent":{"type":"string"},"since_seq":{"type":"integer","minimum":0},"full":{"type":"boolean"},"limit":{"type":"integer","minimum":0},"max_chars":{"type":"integer","minimum":0}},"required":["member_token"]}""";
    const string LeadReadSchema = """{"type":"object","properties":{"since_seq":{"type":"integer","minimum":0},"limit":{"type":"integer","minimum":1,"maximum":50}}}""";
    const string LeaveSchema = """{"type":"object","properties":{"member_token":{"type":"string"}},"required":["member_token"]}""";
    const string MemberWakeSchema = """{"type":"object","properties":{"member_token":{"type":"string"},"codex_thread_id":{"type":"string"},"codex_home":{"type":"string"}},"required":["member_token","codex_thread_id"]}""";

    public static async Task<int> RunAsync(StateDirectory state, bool testProfile)
    {
        var externalOnly = Environment.GetEnvironmentVariable("ATF_EXTERNAL_ONLY") == "1";
        var client = new IpcClient(state, new SpikeLimits());
        var workspace = Path.GetFullPath(Environment.CurrentDirectory);
        var parentId = Environment.GetEnvironmentVariable("WIN_AGENT_TEAMS_PARENT_ID") ?? ParentPid().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var bindingKey = $"identity=team-lead\nparent={parentId}\ncwd={workspace}";
        string? sessionId = null;
        async Task<IpcResponse> EnsureSessionAsync(CancellationToken cancellationToken)
        {
            if (sessionId is not null)
            {
                return new IpcResponse(true);
            }
            var started = await client.SendAsync(new IpcRequest { Op = IpcProtocol.SessionStart, Workspace = workspace, BindingKey = bindingKey }, cancellationToken);
            if (started.Ok)
            {
                sessionId = started.Session?.SessionId;
                await BindWakeAsync(cancellationToken);
            }
            return started;
        }
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
                    await BindWakeAsync(cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        if (!externalOnly)
        {
            await RegisterWakeAsync(CancellationToken.None);
            await EnsureSessionAsync(CancellationToken.None);
        }
        var tools = new List<Tool>
        {
            new() { Name = "submit_job", Description = "Durably submit a task to an agent (claude, codex or pi) run by the AgentTeamForge daemon. Returns the job; poll get_job for the result.", InputSchema = Parse(testProfile ? TestSubmitSchema : SubmitSchema) },
            new() { Name = "get_job", Description = "Read a job's status, result output and native session_id.", InputSchema = Parse(GetSchema) },
            new() { Name = "get_job_output", Description = "Read live stdout/stderr log bytes from a job, starting at an absolute offset. Use next_offset to continue.", InputSchema = Parse(OutputSchema) },
            new() { Name = "stop_job", Description = "Cancel a queued or running job. A finished job is returned unchanged.", InputSchema = Parse(GetSchema) },
            new() { Name = "follow_up", Description = "Resume a job's native agent session. A running job needs interrupt=true; otherwise follow_up returns parent_not_ready.", InputSchema = Parse(FollowUpSchema) },
            new() { Name = "list_jobs", Description = "List jobs, newest first, one bounded page at a time.", InputSchema = Parse(ListSchema) },
            new() { Name = "session_info", Description = "Report this lead's session and recoverable sessions in its workspace.", InputSchema = Parse(EmptySchema) },
            new() { Name = "resume_session", Description = "Adopt a prior lead session and its jobs after a restart.", InputSchema = Parse(ResumeSchema) },
            new() { Name = "close_team", Description = "Close this lead session and revoke all external member tokens.", InputSchema = Parse(EmptySchema) },
            new() { Name = "register_codex_wake", Description = "Register this Codex conversation for native job notices before submitting jobs. Read CODEX_THREAD_ID with a shell tool and pass it here; Codex does not always pass it to MCP servers.", InputSchema = Parse(CodexWakeSchema) },
            new() { Name = "create_join_ticket", Description = "Issue a one-time, ten-minute ticket for a manually started member of this lead session.", InputSchema = Parse(TicketSchema) },
            new() { Name = "join_team", Description = "Join a lead session using its one-time ticket. Save member_token for subsequent calls.", InputSchema = Parse(JoinSchema) },
            new() { Name = "external_send", Description = "Send a durable message to the joined lead using member_token.", InputSchema = Parse(MemberSendSchema) },
            new() { Name = "external_read", Description = "Read this member's inbox using member_token and an optional cursor.", InputSchema = Parse(MemberReadSchema) },
            new() { Name = "external_set_wake", Description = "Opt this member into Codex queue notices. Pass an empty codex_thread_id to clear.", InputSchema = Parse(MemberWakeSchema) },
            new() { Name = "leave_team", Description = "Revoke this external membership without stopping its process.", InputSchema = Parse(LeaveSchema) },
            new() { Name = "send_message", Description = "Send a durable message to an external member of this lead session.", InputSchema = Parse(LeadSendSchema) },
            new() { Name = "read_messages", Description = "Read durable messages from external members of this lead session.", InputSchema = Parse(LeadReadSchema) },
            new() { Name = "job_submit", Description = "Durably submit a job to the AgentTeamForge daemon (spike).", InputSchema = Parse(testProfile ? TestSubmitSchema : SubmitSchema) },
            new() { Name = "job_get", Description = "Read a job's committed state and result (spike).", InputSchema = Parse(GetSchema) },
            new() { Name = "job_list", Description = "List your jobs' committed state, newest first, one bounded page at a time (read-only, spike).", InputSchema = Parse(ListSchema) },
        };
        if (externalOnly)
        {
            tools.RemoveAll(tool => tool.Name is not ("join_team" or "external_send" or "external_read" or "external_set_wake" or "leave_team"));
        }

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
                    if (externalOnly)
                    {
                        var (memberRequest, rejection) = Map(call.Name, args, testProfile);
                        var memberResponse = memberRequest is null || tools.All(tool => tool.Name != call.Name)
                            ? new IpcResponse(false, rejection ?? IpcProtocol.UnknownOp)
                            : await client.SendAsync(memberRequest, cancellationToken);
                        return new CallToolResult
                        {
                            IsError = !memberResponse.Ok,
                            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(memberResponse, IpcJson.Default.IpcResponse) }],
                        };
                    }
                    await RegisterWakeAsync(cancellationToken);
                    var started = await EnsureSessionAsync(cancellationToken);
                    IpcResponse response;
                    if (!started.Ok)
                    {
                        response = started;
                    }
                    else if (call.Name == "session_info")
                    {
                        response = await client.SendAsync(new IpcRequest { Op = IpcProtocol.SessionInfo, LeadSessionId = sessionId, Workspace = workspace }, cancellationToken);
                    }
                    else if (call.Name == "resume_session")
                    {
                        var requested = String(args, "session_id");
                        response = requested is null ? new IpcResponse(false, JobErrors.InvalidRequest)
                            : await client.SendAsync(new IpcRequest { Op = IpcProtocol.SessionResume, LeadSessionId = requested, Workspace = workspace, BindingKey = bindingKey }, cancellationToken);
                        if (response.Ok)
                        {
                            sessionId = response.Session!.SessionId;
                            await BindWakeAsync(cancellationToken);
                        }
                    }
                    else if (call.Name == "register_codex_wake")
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
                            await BindWakeAsync(cancellationToken);
                        }
                    }
                    else if (call.Name == "close_team")
                    {
                        response = await client.SendAsync(new IpcRequest { Op = IpcProtocol.SessionClose, LeadSessionId = sessionId, Workspace = workspace }, cancellationToken);
                        if (response.Ok)
                        {
                            sessionId = null;
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
                            : await client.SendAsync(ipc.Op is IpcProtocol.ExternalJoin or IpcProtocol.ExternalSend or IpcProtocol.ExternalRead
                                or IpcProtocol.ExternalSetWake or IpcProtocol.ExternalLeave ? ipc
                                : ipc with { LeadSessionId = sessionId, Workspace = workspace }, cancellationToken);
                    }
                    return new CallToolResult
                    {
                        IsError = !response.Ok,
                        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(response, IpcJson.Default.IpcResponse) }],
                    };
                },
            },
        };

        async Task BindWakeAsync(CancellationToken cancellationToken)
        {
            if (sessionId is not null && wakeTarget?.WakeKey is not null && wakeGeneration is long generation)
            {
                await client.SendAsync(new IpcRequest
                {
                    Op = IpcProtocol.SessionBindWake,
                    LeadSessionId = sessionId,
                    Workspace = workspace,
                    WakeKey = wakeTarget.WakeKey,
                    WakeGeneration = generation
                }, cancellationToken);
            }
        }

        if (!externalOnly)
        {
            await BindWakeAsync(CancellationToken.None);
        }
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
                TimeoutSeconds = Integer(args, "timeout_s"),
                QueueTtlSeconds = Integer(args, "queue_ttl_s"),
                Behavior = testProfile ? String(args, "behavior") : null,
                Hold = testProfile && args.TryGetValue("hold", out var hold) && hold.ValueKind == JsonValueKind.True,
            }, null),
            "job_get" or "get_job" => (new IpcRequest { Op = IpcProtocol.JobGet, JobId = String(args, "job_id") }, null),
            "get_job_output" => (new IpcRequest { Op = IpcProtocol.JobOutput, JobId = String(args, "job_id"), Offset = Long(args, "offset"), MaxBytes = Integer(args, "max_bytes") }, null),
            "stop_job" => (new IpcRequest { Op = IpcProtocol.JobStop, JobId = String(args, "job_id") }, null),
            "follow_up" => (new IpcRequest
            {
                Op = IpcProtocol.JobFollowUp,
                JobId = String(args, "job_id"),
                Instruction = String(args, "instruction"),
                IdempotencyKey = String(args, "idempotency_key"),
                Interrupt = args.TryGetValue("interrupt", out var interrupt) && interrupt.ValueKind == JsonValueKind.True,
                TimeoutSeconds = Integer(args, "timeout_s"),
                QueueTtlSeconds = Integer(args, "queue_ttl_s"),
            }, null),
            "job_list" or "list_jobs" => ListRequest(args),
            "create_join_ticket" => (new IpcRequest { Op = IpcProtocol.ExternalTicket, MemberName = String(args, "name"), Note = String(args, "note") }, null),
            "join_team" => (new IpcRequest { Op = IpcProtocol.ExternalJoin, LeadSessionId = String(args, "session_id"), TicketToken = String(args, "token") }, null),
            "external_send" => (new IpcRequest { Op = IpcProtocol.ExternalSend, MemberToken = String(args, "member_token"), Text = String(args, "text") }, null),
            "external_read" => (new IpcRequest { Op = IpcProtocol.ExternalRead, MemberToken = String(args, "member_token"), FromAgent = String(args, "from_agent"), SinceSeq = Long(args, "since_seq"), Full = args.TryGetValue("full", out var full) && full.ValueKind == JsonValueKind.True, Limit = Integer(args, "limit"), MaxChars = Integer(args, "max_chars") }, null),
            "external_set_wake" => (new IpcRequest { Op = IpcProtocol.ExternalSetWake, MemberToken = String(args, "member_token"), CodexThreadId = String(args, "codex_thread_id"), WakeHome = String(args, "codex_home") ?? Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") }, null),
            "leave_team" => (new IpcRequest { Op = IpcProtocol.ExternalLeave, MemberToken = String(args, "member_token") }, null),
            "send_message" => (new IpcRequest { Op = IpcProtocol.ExternalLeadSend, MemberName = String(args, "to"), Text = String(args, "text") }, null),
            "read_messages" => (new IpcRequest { Op = IpcProtocol.ExternalLeadRead, SinceSeq = Long(args, "since_seq"), Limit = Integer(args, "limit") }, null),
            _ => (null, IpcProtocol.UnknownOp),
        };

    /// <summary>
    /// Present optional fields must match the advertised schema type. A null or wrongly typed
    /// status/cursor is rejected here instead of being read as absent (no filter / first page).
    /// </summary>
    static (IpcRequest?, string?) ListRequest(IDictionary<string, JsonElement> args) =>
        OptionalString(args, "status", out var status) && OptionalString(args, "cursor", out var cursor)
            && OptionalString(args, "backend", out var backend) && OptionalString(args, "since", out var since)
            && (!args.TryGetValue("all_workspace", out var all) || all.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? (new IpcRequest
            {
                Op = IpcProtocol.JobList,
                Status = status,
                Backend = backend,
                Since = since,
                Limit = Integer(args, "limit"),
                Cursor = cursor,
                AllWorkspace = args.TryGetValue("all_workspace", out var scope) && scope.ValueKind == JsonValueKind.True
            }, null)
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

    static long? Long(IDictionary<string, JsonElement> args, string name) =>
        !args.TryGetValue(name, out var value) ? null
        : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : -1;

    static int ParentPid()
    {
        try
        {
            var line = File.ReadLines("/proc/self/status").FirstOrDefault(line => line.StartsWith("PPid:", StringComparison.Ordinal));
            return line is not null && int.TryParse(line.AsSpan(5).Trim(), out var pid) ? pid : Environment.ProcessId;
        }
        catch (IOException) { return Environment.ProcessId; }
    }

    static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
