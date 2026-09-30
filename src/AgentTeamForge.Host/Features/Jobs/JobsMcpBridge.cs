using System.Text.Json;
using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Features.Setup;
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
    internal static string? NativeKind(string? hostKind, string? claudeId, string? codexId) => hostKind switch
    {
        "codex" => string.IsNullOrWhiteSpace(codexId) ? null : "codex",
        "claude" => string.IsNullOrWhiteSpace(claudeId) ? null : "claude",
        _ => !string.IsNullOrWhiteSpace(claudeId) ? "claude" : !string.IsNullOrWhiteSpace(codexId) ? "codex" : null
    };
    const string LimitProperties = """
          "timeout_s":{"type":"integer","minimum":1,"maximum":86400,"description":"Cancel the job (reason timeout) this many seconds after it starts running."},
          "queue_ttl_s":{"type":"integer","minimum":1,"maximum":86400,"description":"Cancel the job (reason queue_ttl) if it has not started this many seconds after acceptance."}
        """;

    const string SubmitProperties = """
          "backend":{"type":"string","enum":["claude","codex","pi","cursor","droid","fake"],"description":"Agent CLI the daemon runs for this job. Cursor and Droid require headless launch mode."},
          "model":{"type":"string","description":"Codex/pi/cursor/droid capability tier: cheapest, low, medium, high, xhigh, max; pi also has medium-fast. Tier mappings are configurable; read the effective table in session_info or Settings. Claude: haiku, sonnet, opus (default), fable. Raw model slugs pass through."},
          "effort":{"type":"string","description":"Explicit effort for Claude or a raw/blank Codex/pi model. A capability tier owns its effort and ignores this override."},
          "herdr_placement":{"type":"string","description":"Optional: herdr-session:<name> (started if stopped). Omit to use the daemon's default."},
          "expected_outputs":{"type":"array","items":{"type":"string"},"maxItems":100,"description":"Expected output paths retained as metadata; does not verify files."},
          "instruction":{"type":"string","description":"Task for the agent."},
          "name":{"type":"string","description":"Optional name for this agent and its web console card."},
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
          "defer":{"type":"boolean","default":true,"description":"Queue durably behind a busy or queued parent. False refuses a busy parent."},
          "replace_if_idle":{"type":"boolean","default":true,"description":"False refuses an idle live interactive agent; dead sessions can still be resumed."},
          "interrupt":{"type":"boolean","description":"If the parent is running, cancel its turn (reason interrupted) and run this prompt in the same session."},
          "model":{"type":"string","description":"Optional replacement model or capability tier. Codex/pi: cheapest, low, medium, high, xhigh, max; pi also has medium-fast. Mappings are configurable; read the effective table in session_info or the web console Settings view. Omit to inherit the resolved parent model."},
          "effort":{"type":"string","description":"Optional effort override; ignored when model is a capability tier. Omit to inherit the parent's effort."},
        """ + LimitProperties + """
        },"required":["job_id","instruction","idempotency_key"]}
        """;

    const string ListSchema = """
        {"type":"object","properties":{
          "status":{"type":"string","enum":["queued","running","completed","failed","needs_reconciliation","cancelled"]},
          "backend":{"type":"string","enum":["fake","claude","codex","pi","cursor","droid"]},
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
    const string LeadSendSchema = """{"type":"object","properties":{"to":{"type":"string","default":"team-lead"},"job_id":{"type":"string","description":"Managed child job to follow up."},"text":{"type":"string"},"idempotency_key":{"type":"string","description":"Required with job_id; reuse on retry."}},"required":["text"]}""";
    const string MemberReadSchema = """{"type":"object","properties":{"member_token":{"type":"string"},"from_agent":{"type":"string"},"since_seq":{"type":"integer","minimum":0},"full":{"type":"boolean"},"limit":{"type":"integer","minimum":0},"max_chars":{"type":"integer","minimum":0}},"required":["member_token"]}""";
    const string LeadReadSchema = """{"type":"object","properties":{"from_agent":{"type":"string"},"since_seq":{"type":"integer","minimum":0},"full":{"type":"boolean"},"limit":{"type":"integer","minimum":0,"maximum":10000},"max_chars":{"type":"integer","minimum":0}}}""";
    const string HumanInputSchema = """{"type":"object","properties":{"question":{"type":"string"},"idempotency_key":{"type":"string","description":"Stable key for this question; reuse it when retrying."}},"required":["question","idempotency_key"]}""";
    const string LeaveSchema = """{"type":"object","properties":{"member_token":{"type":"string"}},"required":["member_token"]}""";
    internal static IpcRequest ClaudeMemberWake(IpcRequest request, IpcRequest? host) => request with
    {
        WakeKind = "claude",
        WakeAddress = host?.WakeKind == "claude" ? host.WakeAddress : null,
        WakeSecret = host?.WakeKind == "claude" ? host.WakeSecret : null,
        WakeHome = host?.WakeKind == "claude" ? host.WakeHome : null
    };

    const string MemberWakeSchema = """{"type":"object","properties":{"member_token":{"type":"string"},"kind":{"type":"string","enum":["claude","codex"]},"codex_thread_id":{"type":"string"},"codex_home":{"type":"string"}},"required":["member_token"]}""";

    public static async Task<int> RunAsync(StateDirectory state, bool testProfile, string? managedContextPath = null)
    {
        string? parentMemberToken = null;
        string? childBinding = null;
        var humanInputAvailable = true;
        if (managedContextPath is not null)
        {
            using var context = JsonDocument.Parse(StateDirectory.ReadPrivateFile(managedContextPath));
            if (context.RootElement.GetProperty("state_dir").GetString() != state.Path)
            {
                throw new StateDirectoryException("managed_context_state_mismatch");
            }
            parentMemberToken = context.RootElement.GetProperty("member_token").GetString();
            childBinding = context.RootElement.GetProperty("binding_key").GetString();
            humanInputAvailable = !context.RootElement.TryGetProperty("human_input_available", out var available)
                || available.GetBoolean();
        }
        var externalOnly = managedContextPath is null && Environment.GetEnvironmentVariable("ATF_EXTERNAL_ONLY") == "1";
        _ = StateDirectory.ReadPrivateFile(state.CredentialFile);
        if (await SetupCommand.StartAsync(new Dictionary<string, string> { ["state-dir"] = state.Path }, quiet: true) != 0)
        {
            return 1;
        }
        var client = new IpcClient(state, new SpikeLimits());
        async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken cancellationToken)
        {
            var response = await client.SendAsync(request, cancellationToken);
            if (response.Error != IpcProtocol.DaemonUnavailable || cancellationToken.IsCancellationRequested)
            {
                return response;
            }

            // Only a pre-request failure is safe to replay. The starter's start.lock
            // serializes concurrent bridges; the retry keeps the original key and identity.
            if (await SetupCommand.StartAsync(new Dictionary<string, string> { ["state-dir"] = state.Path }, quiet: true) != 0)
            {
                return response;
            }
            return await client.SendAsync(request, cancellationToken);
        }
        var workspace = Path.GetFullPath(Environment.CurrentDirectory);
        var parentId = Environment.GetEnvironmentVariable("WIN_AGENT_TEAMS_PARENT_ID") ?? ParentPid().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var bindingKey = childBinding ?? $"identity=team-lead\nparent={parentId}\ncwd={workspace}";
        var managedJobId = childBinding is not null && childBinding.StartsWith("managed-child:", StringComparison.Ordinal)
            ? childBinding["managed-child:".Length..] : null;
        var nativeId = Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID");
        var nativeKind = NativeKind(HostSessionWake.CurrentHost()?.Kind, nativeId, Environment.GetEnvironmentVariable("CODEX_THREAD_ID"));
        nativeId = nativeKind == "claude" ? nativeId : nativeKind == "codex" ? Environment.GetEnvironmentVariable("CODEX_THREAD_ID") : null;
        var nativeHome = nativeKind == "claude"
            ? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        nativeHome = Path.GetFullPath(nativeHome, workspace);
        if (string.IsNullOrWhiteSpace(nativeId)) { nativeId = null; nativeKind = null; }
        string? sessionId = null;
        async Task<IpcResponse> EnsureSessionAsync(CancellationToken cancellationToken)
        {
            if (sessionId is not null)
            {
                return new IpcResponse(true);
            }
            var started = await SendAsync(new IpcRequest { Op = IpcProtocol.SessionStart, Workspace = workspace, BindingKey = bindingKey, NativeKind = nativeKind, NativeSessionId = nativeId, NativeHome = nativeId is null ? null : nativeHome }, cancellationToken);
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
            if (wakeTarget is null)
            {
                return;
            }

            // Wake is best effort: a missing daemon or credential must not stop the bridge or fail job calls.
            try
            {
                if (wakeGeneration is not null && sessionId is not null)
                {
                    var status = await SendAsync(new IpcRequest
                    { Op = IpcProtocol.WakeStatus, LeadSessionId = sessionId, Workspace = workspace }, cancellationToken);
                    if (!status.Ok)
                    {
                        Console.Error.WriteLine($"[atf-bridge] wake status failed: {status.Error}");
                        return;
                    }
                    switch (RepairWake(status.WakeStatus, wakeTarget, wakeGeneration))
                    {
                        case WakeRepair.Keep: return;
                        case WakeRepair.Adopt: wakeGeneration = status.WakeStatus!.Generation; return;
                    }
                    wakeGeneration = null;
                }
                var registration = await SendAsync(wakeTarget with { LeadSessionId = sessionId, Workspace = workspace, JobId = managedJobId }, cancellationToken);
                if (registration.Ok && registration.WakeGeneration is long generation)
                {
                    wakeGeneration = generation;
                    await BindWakeAsync(cancellationToken);
                }
                else { Console.Error.WriteLine($"[atf-bridge] wake registration failed: {registration.Error}"); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[atf-bridge] wake registration failed: {ex.GetType().Name}");
            }
        }
        if (!externalOnly)
        {
            await EnsureSessionAsync(CancellationToken.None);
            await RegisterWakeAsync(CancellationToken.None);
        }
        var tools = new List<Tool>
        {
            new() { Name = "submit_job", Description = "Durably submit a task to an agent (claude, codex, pi, cursor or droid) run by the AgentTeamForge daemon. Read get_job for the result; registered native wake provides best-effort notices.", InputSchema = Parse(testProfile ? TestSubmitSchema : SubmitSchema) },
            new() { Name = "get_job", Description = "Read a job's status, result output and native session_id.", InputSchema = Parse(GetSchema) },
            new() { Name = "get_job_output", Description = "Read live stdout/stderr log bytes from a job, starting at an absolute offset. Use next_offset to continue.", InputSchema = Parse(OutputSchema) },
            new() { Name = "stop_job", Description = "Cancel a queued or running job. A finished job is returned unchanged. Refusals include error_detail.", InputSchema = Parse(GetSchema) },
            new() { Name = "follow_up", Description = "Send a new turn to a job's native agent session. A live Codex TUI queues it even while busy; other busy turns wait durably. interrupt=true cancels the current turn first; defer=false refuses a busy parent.", InputSchema = Parse(FollowUpSchema) },
            new() { Name = "list_backends", Description = "Discover configured backends, model choices, cached native models, effective tiers and launch mode on this daemon.", InputSchema = Parse(EmptySchema) },
            new() { Name = "interrupt_job", Description = "Interrupt a turn without sending another prompt. The native session remains resumable with follow_up or revive_agent.", InputSchema = Parse(GetSchema) },
            new() { Name = "revive_agent", Description = "Resume a dead or finished agent using its recorded native session, backend and worktree. Requires a new instruction and idempotency key; uncertain live sessions remain fenced.", InputSchema = Parse(FollowUpSchema) },
            new() { Name = "stop_agent", Description = "Close an owned interactive agent. If its turn is still recorded as running, waits briefly for the completion to be recorded; if it is still running, refuses with a retryable error_detail and changes nothing (use stop_job to cancel the turn, or interrupt_job to keep the agent). Refusals include error_detail.", InputSchema = Parse(GetSchema) },
            new() { Name = "remove_worktree", Description = "Remove an owned finished job's git worktree if nothing would be lost (no unpushed commits, no dirty or unknown ignored files, no live agent). force=true overrides dirty/ignored files only; dry_run=true reports without removing.", InputSchema = Parse("""{"type":"object","properties":{"job_id":{"type":"string"},"force":{"type":"boolean"},"dry_run":{"type":"boolean"}},"required":["job_id"]}""") },
            new() { Name = "get_job_activity", Description = "Read structured progress with a monotonic after_cursor and bounded limit.", InputSchema = Parse("""{"type":"object","properties":{"job_id":{"type":"string"},"after_cursor":{"type":"integer","minimum":0},"limit":{"type":"integer","minimum":1,"maximum":50}},"required":["job_id"]}""") },
            new() { Name = "list_jobs", Description = "List jobs, newest first, one bounded page at a time.", InputSchema = Parse(ListSchema) },
            new() { Name = "set_session_name", Description = "Set a display name for this lead session (shown in the web console). Empty clears it.", InputSchema = Parse("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"],"additionalProperties":false}""") },
            new() { Name = "session_info", Description = "Report this lead's session and recoverable sessions in its workspace.", InputSchema = Parse(EmptySchema) },
            new() { Name = "resume_session", Description = "Adopt a prior lead session and its jobs after a restart.", InputSchema = Parse(ResumeSchema) },
            new() { Name = "close_team", Description = "Close this lead session and revoke all external member tokens.", InputSchema = Parse(EmptySchema) },
            new() { Name = "register_codex_wake", Description = "Register this Codex conversation for native job notices before submitting jobs. Read CODEX_THREAD_ID with a shell tool and pass it here; Codex does not always pass it to MCP servers.", InputSchema = Parse(CodexWakeSchema) },
            new() { Name = "clear_wake", Description = "Clear this lead's native wake registration. Unread jobs remain available and can be rebound later.", InputSchema = Parse(EmptySchema) },
            new() { Name = "wake_status", Description = "Show this lead's current native wake registration.", InputSchema = Parse(EmptySchema) },
            new() { Name = "create_join_ticket", Description = "Issue a one-time, ten-minute ticket for a manually started member of this lead session.", InputSchema = Parse(TicketSchema) },
            new() { Name = "join_team", Description = "Join an AgentTeamForge external team, separate from Codex built-in collaboration. Save member_token. Use mcp__agentteamforge__external_read to read work and mcp__agentteamforge__external_send to reply.", InputSchema = Parse(JoinSchema) },
            new() { Name = "external_send", Description = "Send a durable message to the joined lead using member_token (mcp__agentteamforge__external_send).", InputSchema = Parse(MemberSendSchema) },
            new() { Name = "external_read", Description = "Read this member's inbox using member_token and an optional cursor (mcp__agentteamforge__external_read).", InputSchema = Parse(MemberReadSchema) },
            new() { Name = "external_set_wake", Description = "Register native member notices (mcp__agentteamforge__external_set_wake): kind=claude uses this host’s own channel; kind=codex uses codex_thread_id. Pass an empty codex_thread_id without kind to clear. No hooks are installed.", InputSchema = Parse(MemberWakeSchema) },
            new() { Name = "leave_team", Description = "Revoke this external membership without stopping its process.", InputSchema = Parse(LeaveSchema) },
            new() { Name = "send_message", Description = "Send to your ATF parent or a joined external member with to=..., or send managed downstream work with job_id=... and idempotency_key=.... A live Codex child receives managed work through codex queue. This tool does not reach win-agent-teams members.", InputSchema = Parse(LeadSendSchema) },
            new() { Name = "read_messages", Description = "Read durable messages from external members of this lead session. Without from_agent, since_seq and next_seq use the lead inbox's durable message position across all senders; each message's seq and the cursors map are per sender (cursors lists only senders in this page). With from_agent, since_seq uses that sender's seq.", InputSchema = Parse(LeadReadSchema) },
            new() { Name = "job_submit", Description = "Durably submit a job to the AgentTeamForge daemon (spike).", InputSchema = Parse(testProfile ? TestSubmitSchema : SubmitSchema) },
            new() { Name = "job_get", Description = "Read a job's committed state and result (spike).", InputSchema = Parse(GetSchema) },
            new() { Name = "job_list", Description = "List your jobs' committed state, newest first, one bounded page at a time (read-only, spike).", InputSchema = Parse(ListSchema) },
        };
        if (ShouldOfferHumanInput(parentMemberToken, humanInputAvailable))
        {
            tools.Add(new() { Name = "request_human_input", Description = "Ask the human owner a question that blocks your task. The question is saved durably; then END YOUR TURN. Never wait on stdin or an approval prompt. The answer resumes this same session in a new turn.", InputSchema = Parse(HumanInputSchema) });
        }
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
                        if (memberRequest?.Op == IpcProtocol.ExternalSetWake && String(args, "kind") == "claude")
                        {
                            memberRequest = ClaudeMemberWake(memberRequest, wakeTarget);
                        }
                        var memberResponse = memberRequest is null || tools.All(tool => tool.Name != call.Name)
                            ? new IpcResponse(false, rejection ?? IpcProtocol.UnknownOp)
                            : await SendAsync(memberRequest, cancellationToken);
                        memberResponse = await BindJoinedClaudeAsync(call.Name, memberResponse, cancellationToken);
                        return new CallToolResult
                        {
                            IsError = !memberResponse.Ok,
                            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(memberResponse.ForMcp(), IpcJson.Default.IpcResponse) }],
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
                        response = await SendAsync(new IpcRequest { Op = IpcProtocol.SessionInfo, LeadSessionId = sessionId, Workspace = workspace }, cancellationToken);
                    }
                    else if (call.Name == "set_session_name")
                    {
                        var (nameRequest, rejection) = Map(call.Name, args, testProfile);
                        response = nameRequest is null ? new IpcResponse(false, rejection)
                            : await SendAsync(nameRequest with { LeadSessionId = sessionId, Workspace = workspace }, cancellationToken);
                    }
                    else if (call.Name == "resume_session")
                    {
                        var requested = String(args, "session_id");
                        response = requested is null ? new IpcResponse(false, JobErrors.InvalidRequest)
                            : await SendAsync(new IpcRequest { Op = IpcProtocol.SessionResume, LeadSessionId = requested, Workspace = workspace, BindingKey = bindingKey, NativeKind = nativeKind, NativeSessionId = nativeId, NativeHome = nativeId is null ? null : nativeHome }, cancellationToken);
                        if (response.Ok)
                        {
                            // An explicit resume takes over the session's wake, even from another live bridge.
                            sessionId = response.Session!.SessionId;
                            wakeGeneration = null;
                            await RegisterWakeAsync(cancellationToken);
                        }
                    }
                    else if (call.Name == "register_codex_wake")
                    {
                        var target = HostSessionWake.ForCodexLead(String(args, "thread_id"), HostSessionWake.CurrentHost(),
                            pid => HostSessionWake.CodexHome(pid),
                            message => Console.Error.WriteLine("[atf-bridge] register_codex_wake: " + message));
                        response = target is null ? new IpcResponse(false, JobErrors.InvalidRequest)
                            : await SendAsync(target with { LeadSessionId = sessionId, Workspace = workspace, JobId = managedJobId }, cancellationToken);
                        if (response.Ok && response.WakeGeneration is long generation)
                        {
                            wakeTarget = target;
                            wakeGeneration = generation;
                            await BindWakeAsync(cancellationToken);
                        }
                    }
                    else if (call.Name == "clear_wake")
                    {
                        response = await SendAsync(new IpcRequest { Op = IpcProtocol.WakeClear, LeadSessionId = sessionId, Workspace = workspace }, cancellationToken);
                        if (response.Ok) { wakeTarget = null; wakeGeneration = null; }
                    }
                    else if (call.Name == "wake_status")
                    {
                        response = await SendAsync(new IpcRequest { Op = IpcProtocol.WakeStatus, LeadSessionId = sessionId, Workspace = workspace }, cancellationToken);
                    }
                    else if (call.Name == "request_human_input" && parentMemberToken is not null)
                    {
                        response = await SendAsync(new IpcRequest
                        {
                            Op = IpcProtocol.HumanInputRequest,
                            MemberToken = parentMemberToken,
                            Text = String(args, "question"),
                            IdempotencyKey = String(args, "idempotency_key"),
                        }, cancellationToken);
                    }
                    else if (call.Name == "close_team")
                    {
                        response = await SendAsync(new IpcRequest { Op = IpcProtocol.SessionClose, LeadSessionId = sessionId, Workspace = workspace }, cancellationToken);
                        if (response.Ok)
                        {
                            sessionId = null;
                        }
                    }
                    else
                    {
                        if (call.Name == "read_messages")
                        {
                            args = WithoutNulls(args);
                        }
                        var invalidReadField = call.Name == "read_messages" ? InvalidReadMessagesField(args) : null;
                        var (ipc, rejection) = Map(call.Name, args, testProfile);
                        ipc = RouteParent(ipc, parentMemberToken);
                        if (ipc?.Op == IpcProtocol.ExternalSetWake && String(args, "kind") == "claude")
                        {
                            ipc = ClaudeMemberWake(ipc, wakeTarget);
                        }
                        if (ipc is not null && wakeTarget is not null && wakeGeneration is not null
                            && ipc.Op is IpcProtocol.JobSubmit or IpcProtocol.JobFollowUp or IpcProtocol.JobGet)
                        {
                            ipc = ipc with { WakeKey = wakeTarget.WakeKey, WakeGeneration = wakeGeneration };
                        }
                        response = invalidReadField is not null
                            ? new IpcResponse(false, JobErrors.InvalidRequest, ErrorDetail: $"Invalid {invalidReadField}.")
                            : ipc is null
                            ? new IpcResponse(false, rejection)
                            : await SendAsync(ipc.Op is IpcProtocol.ExternalJoin or IpcProtocol.ExternalSend or IpcProtocol.ExternalRead
                                or IpcProtocol.ExternalSetWake or IpcProtocol.ExternalLeave ? ipc
                                : ipc with { LeadSessionId = sessionId, Workspace = workspace }, cancellationToken);
                    }
                    response = await BindJoinedClaudeAsync(call.Name, response, cancellationToken);
                    return new CallToolResult
                    {
                        IsError = !response.Ok,
                        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(response.ForMcp(), IpcJson.Default.IpcResponse) }],
                    };
                },
            },
        };

        async Task<IpcResponse> BindJoinedClaudeAsync(string name, IpcResponse response, CancellationToken cancellationToken)
        {
            if (name != "join_team" || !response.Ok || response.MemberToken is not { } memberToken) { return response; }
            if (wakeTarget?.WakeKind != "claude")
            {
                return response with { ErrorDetail = "Native Claude channel unavailable; use external_read or register Codex wake." };
            }
            var registered = await SendAsync(ClaudeMemberWake(new IpcRequest
            { Op = IpcProtocol.ExternalSetWake, MemberToken = memberToken }, wakeTarget), cancellationToken);
            return response with
            {
                WakeGeneration = registered.WakeGeneration,
                ErrorDetail = registered.Ok ? null : "Claude wake unavailable; use external_read. " + registered.Error
            };
        }

        async Task BindWakeAsync(CancellationToken cancellationToken)
        {
            if (sessionId is not null && wakeTarget?.WakeKey is not null && wakeGeneration is long generation)
            {
                var bound = await SendAsync(new IpcRequest
                {
                    Op = IpcProtocol.SessionBindWake,
                    LeadSessionId = sessionId,
                    Workspace = workspace,
                    WakeKey = wakeTarget.WakeKey,
                    WakeGeneration = generation
                }, cancellationToken);
                if (!bound.Ok) { Console.Error.WriteLine($"[atf-bridge] wake binding failed: {bound.Error}"); }
            }
        }

        if (!externalOnly)
        {
            await BindWakeAsync(CancellationToken.None);
        }
        await using var server = McpServer.Create(new StdioServerTransport("agentteamforge"), options);
        using var relayLifetime = new CancellationTokenSource();
        var relay = ClaudeWakeRelay.RunAsync(wakeTarget, client, relayLifetime.Token,
            managedJobId, () => sessionId, workspace);
        try { await server.RunAsync(); }
        finally { await relayLifetime.CancelAsync(); await relay; }
        return 0;
    }

    /// <summary>Optional read_messages arguments sent as JSON null mean "not given", as before validation named fields.</summary>
    internal static Dictionary<string, JsonElement> WithoutNulls(IDictionary<string, JsonElement> args) =>
        args.Where(arg => arg.Value.ValueKind != JsonValueKind.Null).ToDictionary();

    internal static string? InvalidReadMessagesField(IDictionary<string, JsonElement> args)
    {
        if (args.TryGetValue("from_agent", out var sender)
            && (sender.ValueKind != JsonValueKind.String || sender.GetString() is not { Length: <= 64 }))
        { return "from_agent"; }
        if (args.TryGetValue("since_seq", out var since)
            && (since.ValueKind != JsonValueKind.Number || !since.TryGetInt64(out var n) || n < 0))
        { return "since_seq"; }
        if (args.TryGetValue("limit", out var limit)
            && (limit.ValueKind != JsonValueKind.Number || !limit.TryGetInt32(out var count) || count is < 0 or > 10000))
        { return "limit"; }
        if (args.TryGetValue("max_chars", out var max)
            && (max.ValueKind != JsonValueKind.Number || !max.TryGetInt32(out var chars) || chars is < 0 or > 65536))
        { return "max_chars"; }
        if (args.TryGetValue("full", out var full) && full.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        { return "full"; }
        return null;
    }

    internal static bool ShouldOfferHumanInput(string? parentMemberToken, bool available) => parentMemberToken is not null && available;

    internal enum WakeRepair { Keep, Adopt, Register }

    /// <summary>Re-register a missing, unusable or legacy binding of this channel. A newer generation of our own key
    /// is adopted, and a usable binding of another channel (a bridge that resumed this session) is never taken back,
    /// so two live bridges cannot keep bumping generations and re-posting notices.</summary>
    internal static WakeRepair RepairWake(AgentTeamForge.DAL.Features.Wake.WakeRegistrationStatus? status,
        IpcRequest target, long? generation)
    {
        if (generation is null || status is not { Registered: true, Usable: true, Key: not null, Generation: not null })
        {
            return WakeRepair.Register;
        }
        if (status.Key != target.WakeKey)
        {
            return status.Kind == target.WakeKind && status.Address == target.WakeAddress ? WakeRepair.Register : WakeRepair.Keep;
        }
        if (status.Kind != target.WakeKind || status.Address != target.WakeAddress) { return WakeRepair.Register; }
        return status.Generation == generation ? WakeRepair.Keep : WakeRepair.Adopt;
    }

    /// <summary>Only the explicit parent recipient uses the injected membership; typos never fall back upstream.</summary>
    internal static IpcRequest? RouteParent(IpcRequest? request, string? memberToken) =>
        request is { Op: IpcProtocol.ExternalLeadSend, MemberName: "team-lead" } && memberToken is not null
            ? new IpcRequest { Op = IpcProtocol.ExternalSend, MemberToken = memberToken, Text = request.Text }
            : request;

    /// <summary>Maps a tool call to one IPC request, or to a rejection code without contacting the daemon.</summary>
    internal static (IpcRequest? Request, string? Rejection) Map(string name, IDictionary<string, JsonElement> args, bool testProfile) =>
        name switch
        {
            "set_session_name" => String(args, "name") is { } sessionName
                ? (new IpcRequest { Op = IpcProtocol.SessionInfo, SessionName = sessionName }, null)
                : (null, JobErrors.InvalidRequest),
            "job_submit" or "submit_job" => (new IpcRequest
            {
                Op = IpcProtocol.JobSubmit,
                IdempotencyKey = String(args, "idempotency_key"),
                Instruction = String(args, "instruction"),
                Backend = String(args, "backend"),
                TargetAgent = String(args, "name"),
                ExpectedOutputs = Strings(args, "expected_outputs"),
                Model = String(args, "model"),
                Effort = String(args, "effort"),
                HerdrPlacement = String(args, "herdr_placement"),
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
            "list_backends" => (new IpcRequest { Op = IpcProtocol.JobCapabilities }, null),
            "interrupt_job" => (new IpcRequest { Op = IpcProtocol.JobStop, JobId = String(args, "job_id"), Interrupt = true }, null),
            "remove_worktree" => (new IpcRequest { Op = IpcProtocol.JobRemoveWorktree, JobId = String(args, "job_id"), Force = Bool(args, "force"), DryRun = Bool(args, "dry_run") }, null),
            "stop_agent" => (new IpcRequest { Op = IpcProtocol.JobStopAgent, JobId = String(args, "job_id") }, null),
            "get_job_activity" => (new IpcRequest { Op = IpcProtocol.JobActivity, JobId = String(args, "job_id"), AfterCursor = Long(args, "after_cursor"), Limit = Integer(args, "limit") }, null),
            "follow_up" or "revive_agent" => (new IpcRequest
            {
                Op = IpcProtocol.JobFollowUp,
                JobId = String(args, "job_id"),
                Instruction = String(args, "instruction"),
                IdempotencyKey = String(args, "idempotency_key"),
                Interrupt = args.TryGetValue("interrupt", out var interrupt) && interrupt.ValueKind == JsonValueKind.True,
                Defer = !args.TryGetValue("defer", out var defer) || defer.ValueKind == JsonValueKind.True,
                ReplaceIfIdle = !args.TryGetValue("replace_if_idle", out var replace) || replace.ValueKind == JsonValueKind.True,
                Model = String(args, "model"),
                Effort = String(args, "effort"),
                TimeoutSeconds = Integer(args, "timeout_s"),
                QueueTtlSeconds = Integer(args, "queue_ttl_s"),
            }, null),
            "job_list" or "list_jobs" => ListRequest(args),
            "create_join_ticket" => (new IpcRequest { Op = IpcProtocol.ExternalTicket, MemberName = String(args, "name"), Note = String(args, "note") }, null),
            "join_team" => (new IpcRequest { Op = IpcProtocol.ExternalJoin, LeadSessionId = String(args, "session_id"), TicketToken = String(args, "token") }, null),
            "external_send" => (new IpcRequest { Op = IpcProtocol.ExternalSend, MemberToken = String(args, "member_token"), Text = String(args, "text") }, null),
            "external_read" => (new IpcRequest { Op = IpcProtocol.ExternalRead, MemberToken = String(args, "member_token"), FromAgent = String(args, "from_agent"), SinceSeq = Long(args, "since_seq"), Full = args.TryGetValue("full", out var full) && full.ValueKind == JsonValueKind.True, Limit = Integer(args, "limit"), MaxChars = Integer(args, "max_chars") }, null),
            "external_set_wake" => (new IpcRequest { Op = IpcProtocol.ExternalSetWake, MemberToken = String(args, "member_token"), CodexThreadId = String(args, "codex_thread_id"), WakeHome = NonEmpty(String(args, "codex_home")) ?? NonEmpty(Environment.GetEnvironmentVariable("CODEX_HOME")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") }, null),
            "leave_team" => (new IpcRequest { Op = IpcProtocol.ExternalLeave, MemberToken = String(args, "member_token") }, null),
            "send_message" => String(args, "job_id") is { } managedJob
                ? (new IpcRequest
                {
                    Op = IpcProtocol.JobFollowUp,
                    JobId = managedJob,
                    Instruction = String(args, "text"),
                    IdempotencyKey = String(args, "idempotency_key"),
                    Defer = true
                }, null)
                : (new IpcRequest { Op = IpcProtocol.ExternalLeadSend, MemberName = String(args, "to") ?? "team-lead", Text = String(args, "text") }, null),
            "read_messages" => (new IpcRequest { Op = IpcProtocol.ExternalLeadRead, FromAgent = String(args, "from_agent"), SinceSeq = Long(args, "since_seq"), Full = args.TryGetValue("full", out var leadFull) && leadFull.ValueKind == JsonValueKind.True, Limit = Integer(args, "limit"), MaxChars = Integer(args, "max_chars") }, null),
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

    static string[]? Strings(IDictionary<string, JsonElement> args, string name) =>
        !args.TryGetValue(name, out var value) ? null
        : value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String)
            ? [.. value.EnumerateArray().Select(x => x.GetString()!)] : [""];

    static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>A present but non-integer (or null) value becomes 0 so the daemon rejects it rather than defaulting.</summary>
    static int? Integer(IDictionary<string, JsonElement> args, string name) =>
        !args.TryGetValue(name, out var value) ? null
        : value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;

    static bool Bool(IDictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.True;

    static long? Long(IDictionary<string, JsonElement> args, string name) =>
        !args.TryGetValue(name, out var value) ? null
        : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : -1;

    static int ParentPid()
    {
        if (OperatingSystem.IsMacOS())
        {
            return DarwinProcess.ParentPid(Environment.ProcessId) ?? Environment.ProcessId;
        }

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
