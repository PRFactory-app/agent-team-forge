using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Terminals;

public sealed class InteractiveTranscriptReaderTests
{
    const string Marker = "atf-corr:turn-1";

    static string ClaudeUser(string text) =>
        $$$"""{"type":"user","isSidechain":false,"sessionId":"claude-native","message":{"role":"user","content":"{{{text}}}"}}""";
    static string ClaudeAssistant(string text, string? stop) =>
        $$$"""{"type":"assistant","isSidechain":false,"sessionId":"claude-native","message":{"role":"assistant","stop_reason":{{{(stop is null ? "null" : "\"" + stop + "\"")}}},"content":[{"type":"text","text":"{{{text}}}"}]}}""";
    static string ClaudeInbox(string text) =>
        $$$"""{"type":"user","isMeta":true,"isSidechain":false,"sessionId":"claude-native","message":{"role":"user","content":"Another Claude session sent a message:\n{{{text}}}"}}""";
    const string ClaudeApiError = """{"type":"assistant","isSidechain":false,"sessionId":"claude-native","isApiErrorMessage":true,"error":"authentication_failed","message":{"role":"assistant","model":"<synthetic>","stop_reason":"end_turn","content":[{"type":"text","text":"Not logged in · Please run /login"}]}}""";
    const string ClaudeConnectionLost = """{"type":"assistant","isSidechain":false,"sessionId":"claude-native","isApiErrorMessage":true,"error":"server_error","message":{"role":"assistant","model":"<synthetic>","stop_reason":"stop_sequence","content":[{"type":"text","text":"API Error: Connection lost mid-response."}]}}""";
    const string ClaudeUsageLimit = """{"type":"assistant","isSidechain":false,"sessionId":"claude-native","timestamp":"2026-09-27T18:04:31Z","isApiErrorMessage":true,"error":"rate_limit","message":{"role":"assistant","model":"<synthetic>","stop_reason":"stop_sequence","content":[{"type":"text","text":"You've hit your monthly spend limit · your session limit resets 8:20pm (Europe/Berlin)"}]}}""";
    const string ClaudeTurnDuration = """{"type":"system","subtype":"turn_duration","durationMs":1000,"isMeta":false,"sessionId":"claude-native"}""";
    const string ClaudeThinkingEnd = """{"type":"assistant","isSidechain":false,"sessionId":"claude-native","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"thinking","thinking":"x"}]}}""";
    const string ClaudeToolResult = """{"type":"user","isSidechain":false,"sessionId":"claude-native","message":{"role":"user","content":[{"type":"tool_result","content":"ok"}]}}""";
    const string ClaudeBackground = """{"type":"user","isSidechain":false,"sessionId":"claude-native","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tool-1","content":"Running in background"}]},"toolUseResult":{"backgroundTaskId":"task-1"}}""";
    const string ClaudeAsyncAgent = """{"type":"user","isSidechain":false,"sessionId":"claude-native","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tool-2","content":[{"type":"text","text":"Async agent launched successfully."}]}]},"toolUseResult":{"isAsync":true,"status":"async_launched","agentId":"agent-1"}}""";
    static string ClaudeQueuedNotification(string id = "task-1") =>
        $$$"""{"type":"attachment","isSidechain":false,"sessionId":"claude-native","attachment":{"type":"queued_command","commandMode":"task-notification","prompt":"<task-notification><task-id>{{{id}}}</task-id><status>completed</status></task-notification>"}}""";
    static string ClaudeNotification(string id = "task-1") => ClaudeUser($"<task-notification><task-id>{id}</task-id><status>completed</status></task-notification>");

    const string CodexMeta = """{"type":"session_meta","payload":{"id":"codex-native","source":"cli"}}""";
    const string CodexStarted = """{"type":"event_msg","payload":{"type":"task_started"}}""";
    const string CodexComplete = """{"type":"event_msg","payload":{"type":"task_complete"}}""";
    static string CodexUser(string text) =>
        $$$"""{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"{{{text}}}"}]}}""";
    static string CodexAssistant(string text) =>
        $$$"""{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"{{{text}}}"}]}}""";

    const string CodexEnvContext = """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context><current_date>2026-10-01</current_date></environment_context>"}],"internal_chat_message_metadata_passthrough":{"content_item_kinds":["environments.environment_context"]}}}""";

    static string CodexEventUser(string text) =>
        $$$"""{"type":"event_msg","payload":{"type":"user_message","message":"{{{text}}}"}}""";
    const string Partial = """{"type":"user","message":{"role":"us""";

    const string PiHeader = """{"type":"session","id":"pi-native"}""";
    static string PiUser(string text) =>
        $$$"""{"type":"message","message":{"role":"user","content":[{"type":"text","text":"{{{text}}}"}]}}""";
    static string PiAssistant(string text, string stop) =>
        $$$"""{"type":"message","message":{"role":"assistant","stopReason":"{{{stop}}}","content":[{"type":"text","text":"{{{text}}}"}]}}""";

    public static TheoryData<InteractiveAgentKind, string[], string?, bool> Turns => new()
    {
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker),
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"tool-1","name":"Bash","input":{"run_in_background":true}}]}}""",
            ClaudeAssistant("waiting", "end_turn")], "waiting", false },
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeBackground,
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"finished"}]},"toolUseResult":{"retrieval_status":"success","task":{"task_id":"task-1","status":"completed"}}}""",
            ClaudeAssistant("DONE", "end_turn")], "DONE", true },
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeBackground, ClaudeAssistant("waiting", "end_turn")], "waiting", false },
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeBackground,
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"stopped"}]},"toolUseResult":{"message":"Successfully stopped task: task-1","task_id":"task-1","task_type":"local_bash"}}""",
            ClaudeAssistant("STOPPED", "end_turn")], "STOPPED", true },
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeBackground, ClaudeAssistant("waiting", "end_turn"), ClaudeNotification()], "waiting", false },
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeBackground, ClaudeAssistant("waiting", "end_turn"), ClaudeNotification(),
            ClaudeAssistant("DONE", "end_turn"), ClaudeUser("human asks more"), ClaudeAssistant("human reply", "end_turn")], "DONE", true },
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeBackground, ClaudeAssistant("waiting", "end_turn"), ClaudeUser("human takes over"),
            ClaudeNotification(), ClaudeAssistant("human reply", "end_turn")], "waiting", false },
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeBackground, ClaudeAssistant("waiting", "end_turn"), ClaudeNotification("foreign-task"),
            ClaudeAssistant("human reply", "end_turn")], "waiting", false },
        // Mid-turn delivery: the notification arrives as a queued_command attachment.
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeBackground, ClaudeAssistant("checking", "tool_use"), ClaudeQueuedNotification(),
            ClaudeToolResult, ClaudeAssistant("DONE", "end_turn")], "DONE", true },
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeBackground, ClaudeQueuedNotification("foreign-task"),
            ClaudeAssistant("waiting", "end_turn")], "waiting", false },
        // Claude 2.1.283 async Agent launch: pending until its agent-ID task notification and the final reply.
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeAsyncAgent, ClaudeAssistant("still running", "end_turn")], "still running", false },
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeAsyncAgent, ClaudeAssistant("still running", "end_turn"), ClaudeNotification("agent-1"),
            ClaudeAssistant("PARENT", "end_turn")], "PARENT", true },
        // Interim commentary alone never completes, even if the TUI goes idle.
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeAssistant("interim", "tool_use"), ClaudeToolResult], "interim", false },
        // A thinking-only end_turn record precedes the final text; it is not completion yet.
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeAssistant("interim", "tool_use"), ClaudeThinkingEnd], "interim", false },
        // Later human input does not alter the completed machine result.
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeAssistant("interim", "tool_use"), ClaudeToolResult,
            ClaudeAssistant("final", "end_turn"), ClaudeUser("human asks more"), ClaudeAssistant("human reply", "end_turn")], "final", true },
        // An interrupted turn is not completed by a later human turn's completion.
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeAssistant("interim", "tool_use"),
            ClaudeUser("human takes over"), ClaudeAssistant("human reply", "end_turn")], "interim", false },
        // A malformed line may be the next turn's boundary: nothing after it is attributed.
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeAssistant("interim", "tool_use"), Partial,
            ClaudeUser("human takes over"), ClaudeAssistant("human reply", "end_turn")], "interim", false },
        // Image-only human input is a boundary too.
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeAssistant("interim", "tool_use"), ClaudeToolResult,
            """{"type":"user","isSidechain":false,"sessionId":"claude-native","message":{"role":"user","content":[{"type":"image","source":{"type":"base64","data":"AA=="}}]}}""",
            ClaudeAssistant("human reply", "end_turn")], "interim", false },
        { InteractiveAgentKind.Pi, [PiHeader, PiUser(Marker), PiAssistant("interim", "toolUse"),
            """{"type":"message","message":{"role":"user","content":[{"type":"image","data":"AA==","mimeType":"image/png"}]}}""",
            PiAssistant("human reply", "stop")], "interim", false },
        { InteractiveAgentKind.Codex, [CodexMeta, CodexStarted, CodexUser(Marker), CodexAssistant("interim")], "interim", false },
        // Codex records the marked input twice (response_item + event_msg, either order); both are the same turn.
        { InteractiveAgentKind.Codex, [CodexMeta, CodexStarted, CodexUser(Marker), CodexEventUser(Marker), CodexAssistant("final"), CodexComplete], "final", true },
        { InteractiveAgentKind.Codex, [CodexMeta, CodexStarted, CodexEventUser(Marker), CodexUser(Marker), CodexAssistant("final"), CodexComplete], "final", true },
        // Codex-injected environment context mid-turn is not user input; the turn still completes.
        { InteractiveAgentKind.Codex, [CodexMeta, CodexStarted, CodexUser(Marker), CodexAssistant("interim"), CodexEnvContext,
            CodexAssistant("final"), CodexComplete], "final", true },
        { InteractiveAgentKind.Codex, [CodexMeta, CodexStarted, CodexUser(Marker), CodexAssistant("interim"), CodexEnvContext,
            CodexUser("human takes over"), CodexAssistant("human reply"), CodexComplete], "interim", false },
        // A next user input ends the turn even without a task_started record.
        { InteractiveAgentKind.Codex, [CodexMeta, CodexStarted, CodexUser(Marker), CodexAssistant("interim"),
            CodexUser("human takes over"), CodexAssistant("human reply"), CodexComplete], "interim", false },
        { InteractiveAgentKind.Codex, [CodexMeta, CodexStarted, CodexUser(Marker), CodexAssistant("interim"), Partial,
            CodexAssistant("later"), CodexComplete], "interim", false },
        { InteractiveAgentKind.Codex, [CodexMeta, CodexStarted, CodexUser(Marker), CodexAssistant("interim"), CodexAssistant("final"),
            CodexComplete, CodexStarted, CodexUser("human asks more"), CodexAssistant("human reply"), CodexComplete], "final", true },
        { InteractiveAgentKind.Codex, [CodexMeta, CodexStarted, CodexUser(Marker), CodexAssistant("interim"),
            CodexStarted, CodexUser("human takes over"), CodexAssistant("human reply"), CodexComplete], "interim", false },
        { InteractiveAgentKind.Pi, [PiHeader, PiUser(Marker), PiAssistant("interim", "toolUse")], "interim", false },
        { InteractiveAgentKind.Pi, [PiHeader, PiUser(Marker), PiAssistant("interim", "toolUse"), PiAssistant("final", "stop"),
            PiUser("human asks more"), PiAssistant("human reply", "stop")], "final", true },
        { InteractiveAgentKind.Pi, [PiHeader, PiUser(Marker), PiAssistant("interim", "toolUse"),
            PiUser("human takes over"), PiAssistant("human reply", "stop")], "interim", false },
    };

    [Theory]
    [MemberData(nameof(Turns))]
    public void Attribution_stops_at_the_next_native_turn_and_requires_completion(InteractiveAgentKind kind, string[] lines, string? message, bool completed)
    {
        using var state = new TempStateDir();
        var (reader, launch, file) = Setup(state.Path, kind);
        File.WriteAllLines(file, lines);

        var transcript = reader.Read(launch, Marker, DateTimeOffset.UtcNow);

        Assert.NotNull(transcript);
        Assert.Equal(message, transcript.Message);
        Assert.Equal(completed, transcript.Completed);
        Assert.DoesNotContain(transcript.Progress, text => text.StartsWith("human", StringComparison.Ordinal));
    }

    [Fact]
    public void Claude_api_error_is_only_terminal_if_retry_does_not_continue_the_turn()
    {
        using var state = new TempStateDir();
        var (reader, launch, file) = Setup(state.Path, InteractiveAgentKind.Claude);
        File.WriteAllLines(file, [ClaudeUser(Marker), ClaudeApiError]);
        var error = reader.Read(launch, Marker, DateTimeOffset.UtcNow);
        Assert.Equal("agent_login_required", error?.ApiError?.Code);
        Assert.Contains("/login", error?.ApiError?.Message);

        File.AppendAllLines(file, [ClaudeAssistant("retry succeeded", "end_turn")]);
        var retried = reader.Read(launch, Marker, DateTimeOffset.UtcNow);
        Assert.Null(retried?.ApiError);
        Assert.True(retried?.Completed);
        Assert.Equal("retry succeeded", retried?.Message);
    }

    // Codex 0.159 rollout of a turn that ended on a rejected credential (trimmed real record).
    static string CodexFailed(int status, string message) =>
        "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"last_agent_message\":null,\"error\":{\"message\":\"" + message
            + "\",\"codex_error_info\":{\"http_connection_failed\":{\"http_status_code\":" + status + "}}}}}";
    const string Codex401 = "unexpected status 401 Unauthorized: Incorrect API key provided: sk-proj-***0000., url: https://api.openai.com/v1/responses, auth error: 401, auth error code: invalid_api_key";

    [Fact]
    public void Codex_turn_ended_by_a_definite_401_is_a_login_error()
    {
        using var state = new TempStateDir();
        var (reader, launch, file) = Setup(state.Path, InteractiveAgentKind.Codex);
        File.WriteAllLines(file, [CodexMeta, CodexStarted, CodexUser(Marker),
            """{"type":"event_msg","payload":{"type":"error","message":"Reconnecting... 2/5 (unexpected status 401 Unauthorized: x)"}}"""]);
        Assert.Null(reader.Read(launch, Marker, DateTimeOffset.UtcNow)?.ApiError); // Still retrying.

        File.AppendAllLines(file, [CodexFailed(401, Codex401)]);
        var error = reader.Read(launch, Marker, DateTimeOffset.UtcNow)?.ApiError;
        Assert.Equal("agent_login_required", error?.Code);
        Assert.Contains("run `codex login`", error?.Message);
        Assert.Contains("unexpected status 401 Unauthorized", error?.Message);
        Assert.True(error?.TurnEnded);

        // Another status, or a 401 Codex's own status contradicts, is a final API error but not a login problem.
        File.WriteAllLines(file, [CodexMeta, CodexStarted, CodexUser(Marker), CodexFailed(500, "unexpected status 500 Internal Server Error")]);
        Assert.Equal("agent_api_error", reader.Read(launch, Marker, DateTimeOffset.UtcNow)?.ApiError?.Code);
        File.WriteAllLines(file, [CodexMeta, CodexStarted, CodexUser(Marker), CodexFailed(502, Codex401)]);
        Assert.Equal("agent_api_error", reader.Read(launch, Marker, DateTimeOffset.UtcNow)?.ApiError?.Code);
        // A later turn's 401 does not belong to this one.
        File.WriteAllLines(file, [CodexMeta, CodexStarted, CodexUser(Marker), CodexAssistant("done"), CodexComplete,
            CodexStarted, CodexUser("human"), CodexFailed(401, Codex401)]);
        var completed = reader.Read(launch, Marker, DateTimeOffset.UtcNow);
        Assert.Null(completed?.ApiError);
        Assert.True(completed?.Completed);
    }

    [Fact]
    public void Codex_turn_ended_by_server_overload_is_a_final_api_error()
    {
        using var state = new TempStateDir();
        var (reader, launch, file) = Setup(state.Path, InteractiveAgentKind.Codex);
        File.WriteAllLines(file, [CodexMeta, CodexStarted, CodexUser(Marker),
            """{"type":"event_msg","payload":{"type":"task_complete","last_agent_message":null,"error":{"message":"Selected model is at capacity. Please try a different model.","codex_error_info":"server_overloaded"}}}"""]);
        var error = reader.Read(launch, Marker, DateTimeOffset.UtcNow)?.ApiError;
        Assert.Equal("agent_api_error", error?.Code);
        Assert.True(error?.TurnEnded);
    }

    [Fact]
    public void Claude_transient_api_error_waits_longer_until_the_turn_has_ended()
    {
        using var state = new TempStateDir();
        var (reader, launch, file) = Setup(state.Path, InteractiveAgentKind.Claude);
        // Real shape: Claude may continue on its own seconds after this record.
        File.WriteAllLines(file, [ClaudeUser(Marker), ClaudeConnectionLost]);
        var open = reader.Read(launch, Marker, DateTimeOffset.UtcNow)?.ApiError;
        Assert.Equal("agent_api_error", open?.Code);
        Assert.False(open?.TurnEnded);
        Assert.True(open?.QuietWindow >= TimeSpan.FromSeconds(30));

        File.AppendAllLines(file, [ClaudeTurnDuration]);
        var ended = reader.Read(launch, Marker, DateTimeOffset.UtcNow)?.ApiError;
        Assert.True(ended?.TurnEnded);
        Assert.Equal(TimeSpan.FromSeconds(1), ended?.QuietWindow);
    }

    [Fact]
    public void Claude_inbox_user_record_is_a_receipt_and_next_inbox_turn_is_a_boundary()
    {
        using var state = new TempStateDir();
        var dir = Directory.CreateDirectory(Path.Combine(state.Path, "claude", "projects", "scratch"));
        File.WriteAllLines(Path.Combine(dir.FullName, "claude-native.jsonl"),
        [
            ClaudeUser("initial"), ClaudeAssistant("ready", "end_turn"),
            ClaudeInbox("follow-up atf-corr:turn-1"), ClaudeAssistant("delivered", "end_turn"),
            ClaudeInbox("later atf-corr:turn-2"), ClaudeAssistant("later", "end_turn")
        ]);
        var receipt = InteractiveTranscriptReader.ReadClaudeSession(Path.Combine(state.Path, "claude"), "claude-native", "turn-1");
        Assert.Equal("delivered", receipt?.Message);
        Assert.True(receipt?.Completed);
    }

    [Fact]
    public void Unreadable_line_after_a_turn_keeps_its_result_but_marks_the_tail_incomplete()
    {
        using var state = new TempStateDir();
        var dir = Directory.CreateDirectory(Path.Combine(state.Path, "claude", "projects", "scratch"));
        File.WriteAllLines(Path.Combine(dir.FullName, "claude-native.jsonl"),
            [ClaudeUser("work " + Marker), ClaudeAssistant("done", "end_turn"), Partial, ClaudeUser("later human turn")]);
        var turn = InteractiveTranscriptReader.ReadClaudeSession(Path.Combine(state.Path, "claude"), "claude-native", "turn-1");
        // Historical attribution is unchanged; only "nothing came after it" is unproven.
        Assert.Equal(("done", true, true, false), (turn?.Message, turn?.Completed, turn?.Incomplete, turn?.Superseded));
    }

    [Fact]
    public void Claude_monthly_spend_limit_maps_to_rate_limit_with_utc_reset()
    {
        using var state = new TempStateDir();
        var (reader, launch, file) = Setup(state.Path, InteractiveAgentKind.Claude);
        File.WriteAllLines(file, [ClaudeUser(Marker), ClaudeUsageLimit, ClaudeTurnDuration]);

        var error = reader.Read(launch, Marker, DateTimeOffset.UtcNow)?.ApiError;

        Assert.Equal("agent_rate_limited", error?.Code);
        Assert.Contains("2026-09-27T18:20:00", error?.Message);
        Assert.True(error?.TurnEnded);
    }

    [Theory]
    // No minutes, rolls over to the next local day.
    [InlineData("2026-09-27T21:00:00Z", "You've hit your session limit · resets 3pm (Europe/Berlin)", "2026-09-28T13:00:00")]
    // Same local day, across the end of summer time (03:00 CET = 02:00Z).
    [InlineData("2026-10-24T22:30:00Z", "Your limit will reset at 3am (Europe/Berlin)", "2026-10-25T02:00:00")]
    [InlineData("2026-09-27T10:00:00Z", "You've hit your session limit · resets 12:30am (America/New_York)", "2026-09-28T04:30:00")]
    public void Claude_limit_reset_times_are_resolved_in_their_zone(string observed, string text, string expectedUtc)
    {
        using var state = new TempStateDir();
        var (reader, launch, file) = Setup(state.Path, InteractiveAgentKind.Claude);
        var limit = ClaudeUsageLimit.Replace("2026-09-27T18:04:31Z", observed)
            .Replace("You've hit your monthly spend limit · your session limit resets 8:20pm (Europe/Berlin)", text);
        File.WriteAllLines(file, [ClaudeUser(Marker), limit, ClaudeTurnDuration]);

        var error = reader.Read(launch, Marker, DateTimeOffset.UtcNow)?.ApiError;

        Assert.Equal("agent_rate_limited", error?.Code);
        Assert.Contains("resets at " + expectedUtc, error?.Message);
    }

    public static TheoryData<InteractiveAgentKind, string[]> Unbound => new()
    {
        // Only a native user record binds the marker; echoes/metadata/partial lines never do.
        { InteractiveAgentKind.Claude, ["""{"type":"last-prompt","isSidechain":false,"sessionId":"claude-native","lastPrompt":"atf-corr:turn-1"}""", ClaudeAssistant("stale", "end_turn")] },
        { InteractiveAgentKind.Claude, [ClaudeUser("old"), ClaudeAssistant("quoting atf-corr:turn-1", "tool_use"), ClaudeAssistant("stale", "end_turn")] },
        { InteractiveAgentKind.Codex, [CodexMeta, CodexStarted, """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"atf-corr:tu""",
            CodexAssistant("stale"), CodexComplete] },
        { InteractiveAgentKind.Pi, [PiHeader, PiUser("old"), PiAssistant("echo atf-corr:turn-1", "stop")] },
    };

    [Theory]
    [MemberData(nameof(Unbound))]
    public void Marker_outside_a_native_user_record_does_not_bind(InteractiveAgentKind kind, string[] lines)
    {
        using var state = new TempStateDir();
        var (reader, launch, file) = Setup(state.Path, kind);
        File.WriteAllLines(file, lines);

        Assert.Null(reader.Read(launch, Marker, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Partially_flushed_line_is_not_evidence_and_later_flush_completes()
    {
        using var state = new TempStateDir();
        var (reader, launch, file) = Setup(state.Path, InteractiveAgentKind.Codex);
        File.WriteAllText(file, string.Join("\n", CodexMeta, CodexStarted, CodexUser(Marker), CodexAssistant("final")) + "\n"
            + """{"type":"event_msg","payload":{"type":"task_comp""");

        var partial = reader.Read(launch, Marker, DateTimeOffset.UtcNow);
        Assert.Equal("codex-native", partial?.SessionId);
        Assert.False(partial?.Completed);

        File.WriteAllLines(file, [CodexMeta, CodexStarted, CodexUser(Marker), CodexAssistant("final"), CodexComplete]);
        var flushed = reader.Read(launch, Marker, DateTimeOffset.UtcNow);
        Assert.True(flushed?.Completed);
        Assert.Equal("final", flushed?.Message);
    }

    [Fact]
    public void Claude_transcript_is_read_from_the_launched_config_dir_not_default_home()
    {
        using var state = new TempStateDir();
        var home = Path.Combine(state.Path, "home");
        var config = Path.Combine(state.Path, "custom-claude");
        var cwd = Directory.CreateDirectory(Path.Combine(state.Path, "work")).FullName;
        var encoded = new string([.. cwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        string[] turn = [ClaudeUser(Marker), ClaudeAssistant("from custom root", "end_turn")];
        Directory.CreateDirectory(Path.Combine(config, "projects", encoded));
        File.WriteAllLines(Path.Combine(config, "projects", encoded, "claude-native.jsonl"), turn);
        // A decoy in the default root would make the lookup ambiguous (null) if it were consulted.
        Directory.CreateDirectory(Path.Combine(home, ".claude", "projects", encoded));
        File.WriteAllLines(Path.Combine(home, ".claude", "projects", encoded, "decoy.jsonl"),
            [ClaudeUser(Marker).Replace("claude-native", "decoy"), ClaudeAssistant("decoy", "end_turn").Replace("claude-native", "decoy")]);
        var env = new Dictionary<string, string?> { ["HOME"] = home, ["CLAUDE_CONFIG_DIR"] = config };
        var reader = new InteractiveTranscriptReader(name => env.GetValueOrDefault(name));

        foreach (var resume in new[] { null, "claude-native" })
        {
            var launch = new InteractiveLaunch(InteractiveAgentKind.Claude, "atftest", cwd, resume, null, Path.Combine(state.Path, "bootstrap"));
            var transcript = reader.Read(launch, Marker, DateTimeOffset.UtcNow);
            Assert.Equal("claude-native", transcript?.SessionId);
            Assert.Equal("from custom root", transcript?.Message);
            Assert.True(transcript?.Completed);
        }

        Assert.Equal(config, ClaudeConfigRoot.Resolve(name => env.GetValueOrDefault(name), cwd));
        env.Remove("CLAUDE_CONFIG_DIR");
        Assert.Equal(Path.Combine(home, ".claude"), ClaudeConfigRoot.Resolve(name => env.GetValueOrDefault(name), cwd));
    }

    [Fact]
    public void Claude_transcript_is_found_under_the_physical_cwd_when_the_launch_cwd_is_a_symlink()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var state = new TempStateDir();
        // Claude names the project after getcwd(), which resolves every link (macOS /tmp is /private/tmp).
        var physical = Path.Combine(CodexPaths.TrustKey(state.Path), "real", "work");
        Directory.CreateDirectory(physical);
        Directory.CreateSymbolicLink(Path.Combine(state.Path, "alias"), Path.Combine(state.Path, "real"));
        var config = Path.Combine(state.Path, "claude");
        var dir = Directory.CreateDirectory(Path.Combine(config, "projects", Slug(physical))).FullName;
        File.WriteAllLines(Path.Combine(dir, "claude-native.jsonl"), [ClaudeUser(Marker), ClaudeAssistant("physical", "end_turn")]);
        var env = new Dictionary<string, string?> { ["HOME"] = Path.Combine(state.Path, "home"), ["CLAUDE_CONFIG_DIR"] = config };
        var reader = new InteractiveTranscriptReader(name => env.GetValueOrDefault(name));
        var launch = new InteractiveLaunch(InteractiveAgentKind.Claude, "atftest", Path.Combine(state.Path, "alias", "work") + "/", null, null,
            Path.Combine(state.Path, "bootstrap"));

        var transcript = reader.Read(launch, Marker, DateTimeOffset.UtcNow);

        Assert.Equal("physical", transcript?.Message);
        Assert.True(transcript?.Completed);
    }

    [Fact]
    public void Claude_transcript_under_a_hashed_long_project_slug_is_matched_by_prefix()
    {
        using var state = new TempStateDir();
        var cwd = Directory.CreateDirectory(Path.Combine(state.Path, new string('a', 120), new string('b', 120))).FullName;
        var config = Path.Combine(state.Path, "claude");
        var slug = Slug(CodexPaths.TrustKey(cwd));
        Assert.True(slug.Length > 200);
        // Claude cuts a long slug to 200 characters and appends '-' plus a hash it may compute differently per version.
        var dir = Directory.CreateDirectory(Path.Combine(config, "projects", slug[..200] + "-1x2y3z")).FullName;
        Directory.CreateDirectory(Path.Combine(config, "projects", slug[..199] + "z-1x2y3z"));
        File.WriteAllLines(Path.Combine(dir, "claude-native.jsonl"), [ClaudeUser(Marker), ClaudeAssistant("long", "end_turn")]);
        var env = new Dictionary<string, string?> { ["HOME"] = Path.Combine(state.Path, "home"), ["CLAUDE_CONFIG_DIR"] = config };
        var launch = new InteractiveLaunch(InteractiveAgentKind.Claude, "atftest", cwd, null, null, Path.Combine(state.Path, "bootstrap"));

        Assert.Equal("long", new InteractiveTranscriptReader(name => env.GetValueOrDefault(name)).Read(launch, Marker, DateTimeOffset.UtcNow)?.Message);
        Assert.Equal([dir], InteractiveTranscriptReader.ClaudeProjectDirectories(Path.Combine(config, "projects"), cwd));
    }

    static string Slug(string path) => new([.. path.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);

    [Fact]
    public void Relative_claude_config_dir_resolves_against_the_launch_working_directory()
    {
        using var state = new TempStateDir();
        var cwd = Directory.CreateDirectory(Path.Combine(state.Path, "work")).FullName;
        var encoded = new string([.. cwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        var dir = Directory.CreateDirectory(Path.Combine(cwd, "rel-claude", "projects", encoded)).FullName;
        File.WriteAllLines(Path.Combine(dir, "claude-native.jsonl"), [ClaudeUser(Marker), ClaudeAssistant("relative root", "end_turn")]);
        var env = new Dictionary<string, string?> { ["HOME"] = Path.Combine(state.Path, "home"), ["CLAUDE_CONFIG_DIR"] = "rel-claude" };
        var reader = new InteractiveTranscriptReader(name => env.GetValueOrDefault(name));
        var launch = new InteractiveLaunch(InteractiveAgentKind.Claude, "atftest", cwd, null, null, Path.Combine(state.Path, "bootstrap"));

        Assert.Equal("relative root", reader.Read(launch, Marker, DateTimeOffset.UtcNow)?.Message);
        Assert.Equal(Path.Combine(cwd, "rel-claude"), ClaudeConfigRoot.Resolve(name => env.GetValueOrDefault(name), cwd));
    }

    [Theory]
    [InlineData(InteractiveAgentKind.Codex)]
    [InlineData(InteractiveAgentKind.Claude)]
    [InlineData(InteractiveAgentKind.Pi)]
    public void Native_child_cannot_bind_inherited_marker_and_late_children_do_not_change_binding(InteractiveAgentKind kind)
    {
        using var state = new TempStateDir();
        var (reader, launch, file) = Setup(state.Path, kind);
        var parent = kind switch
        {
            InteractiveAgentKind.Codex => new[] { CodexMeta, CodexUser(Marker), CodexAssistant("parent"), CodexComplete },
            InteractiveAgentKind.Claude => [ClaudeUser(Marker), ClaudeAssistant("parent", "end_turn")],
            _ => [PiHeader, PiUser(Marker), PiAssistant("parent", "stop")],
        };
        var child = string.Join('\n', parent).Replace("parent", "child")
            .Replace("codex-native", "child-native").Replace("claude-native", "child-native").Replace("pi-native", "child-native")
            .Replace("\"source\":\"cli\"", "\"source\":{\"subagent\":{\"thread_spawn\":{\"parent_thread_id\":\"codex-native\",\"depth\":1}}}")
            .Replace("\"isSidechain\":false", "\"isSidechain\":true")
            .Replace("\"type\":\"session\"", "\"type\":\"session\",\"parentSession\":\"parent.jsonl\"");
        var childFile = Path.Combine(Path.GetDirectoryName(file)!, "rollout-child.jsonl");
        File.WriteAllText(childFile, child + "\n");
        Assert.Null(reader.Read(launch, Marker, DateTimeOffset.UtcNow));
        File.WriteAllLines(file, parent);
        Assert.Equal("parent", reader.Read(launch, Marker, DateTimeOffset.UtcNow)?.Message);
        Assert.Equal(file, launch.NativeTranscript?.Path);

        // A later child, even one with unavailable ancestry, must not trigger rediscovery.
        File.WriteAllLines(Path.Combine(Path.GetDirectoryName(file)!, "rollout-late.jsonl"), parent);
        File.AppendAllLines(file, parent.Skip(kind == InteractiveAgentKind.Claude ? 0 : 1)
            .Select(line => line.Replace(Marker, "atf-corr:turn-2").Replace("parent", "follow-up")));
        Assert.Equal("follow-up", reader.Read(launch, "atf-corr:turn-2", DateTimeOffset.UtcNow)?.Message);
        Assert.Equal(file, launch.NativeTranscript?.Path);
    }

    [Fact]
    public async Task Fresh_launch_retains_real_reader_binding_for_follow_up_in_same_pane()
    {
        using var state = new TempStateDir();
        var (reader, fixture, file) = Setup(state.Path, InteractiveAgentKind.Codex);
        var control = new RetainedControl();
        var backend = new HerdrInteractiveBackend(control, reader, InteractiveAgentKind.Codex, state.Path);
        File.WriteAllLines(file, [CodexMeta, CodexUser(Marker), CodexAssistant("first"), CodexComplete]);
        var first = backend.Start(new BackendRequest("one", "turn-1", "first", "") { WorkingDirectory = fixture.WorkingDirectory });
        await first.DeliverAsync(CancellationToken.None);
        await foreach (var _ in first.ReadEvidenceAsync(CancellationToken.None)) { }
        await first.DisposeAsync();
        File.WriteAllLines(Path.Combine(Path.GetDirectoryName(file)!, "rollout-decoy.jsonl"),
            [CodexMeta.Replace("codex-native", "decoy"), CodexUser("atf-corr:turn-2"), CodexAssistant("wrong"), CodexComplete]);
        File.AppendAllLines(file, [CodexUser("atf-corr:turn-2"), CodexAssistant("bound follow-up"), CodexComplete]);
        await using var next = backend.Start(new BackendRequest("two", "turn-2", "follow-up", "")
        { WorkingDirectory = fixture.WorkingDirectory, ResumeSessionId = "codex-native" });
        await next.DeliverAsync(CancellationToken.None);
        var evidence = new List<BackendEvidence>();
        await foreach (var item in next.ReadEvidenceAsync(CancellationToken.None)) { evidence.Add(item); }
        Assert.Contains(new BackendEvidence.Result("turn-2", "bound follow-up"), evidence);
        Assert.Equal(1, control.Starts);
    }

    sealed class RetainedControl : IHerdrAgentControl
    {
        public int Starts { get; private set; }
        public Task StartAsync(InteractiveLaunch launch, CancellationToken token) { Starts++; return Task.CompletedTask; }
        public Task PromptAsync(InteractiveLaunch launch, string prompt, CancellationToken token) => Task.CompletedTask;
        public Task InterruptAsync(InteractiveLaunch launch, CancellationToken token) => Task.CompletedTask;
        public Task<InteractiveAgentStatus> StatusAsync(InteractiveLaunch launch, CancellationToken token) => Task.FromResult(InteractiveAgentStatus.Done);
        public void StopOwned(InteractiveLaunch launch) { }
    }

    [Fact]
    public void Claude_binds_after_long_metadata_preamble()
    {
        using var state = new TempStateDir();
        var (reader, launch, file) = Setup(state.Path, InteractiveAgentKind.Claude);
        // Real Claude transcripts can put a dozen mode/attachment/snapshot records before the first message.
        var preamble = Enumerable.Range(0, 12).Select(i => i % 2 == 0
            ? $$$"""{"type":"file-history-snapshot","messageId":"m{{{i}}}"}"""
            : """{"type":"attachment","isSidechain":false,"sessionId":"claude-native","attachment":{"type":"date"}}""");
        File.WriteAllLines(file, [.. preamble, ClaudeUser(Marker), ClaudeAssistant("parent", "end_turn")]);
        Assert.Equal("parent", reader.Read(launch, Marker, DateTimeOffset.UtcNow)?.Message);
        Assert.Equal(file, launch.NativeTranscript?.Path);
    }

    [Fact]
    public void Missing_ancestry_is_explicit_uncertainty_even_with_one_matching_file()
    {
        using var state = new TempStateDir();
        var (reader, launch, file) = Setup(state.Path, InteractiveAgentKind.Codex);
        File.WriteAllLines(file, [CodexMeta.Replace(",\"source\":\"cli\"", ""), CodexUser(Marker), CodexAssistant("unverified"), CodexComplete]);
        Assert.Equal("interactive_binding_unverified", reader.Read(launch, Marker, DateTimeOffset.UtcNow)?.BindingError);
        Assert.Null(launch.NativeTranscript);
    }

    [Fact]
    public void Discovery_checks_beyond_200_decoys_and_reports_true_ambiguity()
    {
        using var state = new TempStateDir();
        var (reader, launch, file) = Setup(state.Path, InteractiveAgentKind.Codex);
        File.WriteAllLines(file, [CodexMeta, CodexUser(Marker), CodexAssistant("parent"), CodexComplete]);
        for (var i = 0; i < 205; i++)
        {
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(file)!, $"rollout-decoy-{i}.jsonl"),
                [CodexMeta.Replace("codex-native", $"decoy-{i}"), CodexUser("unrelated")]);
        }
        Assert.Equal("parent", reader.Read(launch, Marker, DateTimeOffset.UtcNow)?.Message);
        var unbound = launch with { NativeTranscript = null };
        File.WriteAllLines(Path.Combine(Path.GetDirectoryName(file)!, "rollout-second.jsonl"),
            [CodexMeta.Replace("codex-native", "another-parent"), CodexUser(Marker), CodexAssistant("wrong"), CodexComplete]);
        Assert.Equal("interactive_binding_ambiguous", reader.Read(unbound, Marker, DateTimeOffset.UtcNow)?.BindingError);
        Assert.Null(unbound.NativeTranscript);
        Assert.Equal("parent", reader.Read(launch, Marker, DateTimeOffset.UtcNow)?.Message);
        var resumed = unbound with { ResumeSessionId = "codex-native" };
        Assert.Equal("parent", reader.Read(resumed, Marker, DateTimeOffset.UtcNow)?.Message);
        File.WriteAllLines(file, [CodexMeta.Replace("codex-native", "replacement"), CodexUser(Marker)]);
        Assert.Equal("interactive_binding_lost", reader.Read(launch, Marker, DateTimeOffset.UtcNow)?.BindingError);
    }

    [Fact]
    public void CodexReaderKeepsDaemonAnchoredHomeAfterEnvironmentChanges()
    {
        using var state = new TempStateDir();
        var relativeHome = Path.GetRelativePath(Environment.CurrentDirectory, Path.Combine(state.Path, "first"));
        var env = new Dictionary<string, string?> { ["HOME"] = state.Path, ["CODEX_HOME"] = relativeHome };
        var reader = new InteractiveTranscriptReader(name => env.GetValueOrDefault(name));
        var dir = Directory.CreateDirectory(Path.Combine(state.Path, "first", "sessions"));
        var file = Path.Combine(dir.FullName, "rollout-test.jsonl");
        File.WriteAllLines(file, [CodexMeta, CodexUser(Marker), CodexAssistant("bound"), CodexComplete]);
        env["CODEX_HOME"] = Path.Combine(state.Path, "other");
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "agent", state.Path, null, null, state.File("bootstrap"));

        Assert.Equal("bound", reader.Read(launch, Marker, DateTimeOffset.UtcNow)?.Message);
    }

    static (InteractiveTranscriptReader Reader, InteractiveLaunch Launch, string File) Setup(string root, InteractiveAgentKind kind)
    {
        var cwd = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        var env = new Dictionary<string, string?>
        {
            ["HOME"] = Path.Combine(root, "home"),
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(root, "claude"),
            ["CODEX_HOME"] = Path.Combine(root, "codex"),
        };
        var encoded = new string([.. cwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        var dir = kind switch
        {
            InteractiveAgentKind.Claude => Path.Combine(root, "claude", "projects", encoded),
            InteractiveAgentKind.Codex => Path.Combine(root, "codex", "sessions", "2026", "09", "26"),
            _ => Path.Combine(root, "pi-sessions", "one"),
        };
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, kind == InteractiveAgentKind.Codex ? "rollout-test.jsonl" : "session.jsonl");
        var launch = new InteractiveLaunch(kind, "atftest", cwd, null, kind == InteractiveAgentKind.Pi ? dir : null, Path.Combine(root, "bootstrap"));
        return (new InteractiveTranscriptReader(name => env.GetValueOrDefault(name)), launch, file);
    }
}
