using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Terminals;

public sealed class InteractiveTranscriptReaderTests
{
    const string Marker = "atf-corr:turn-1";

    static string ClaudeUser(string text) =>
        $$$"""{"type":"user","sessionId":"claude-native","message":{"role":"user","content":"{{{text}}}"}}""";
    static string ClaudeAssistant(string text, string? stop) =>
        $$$"""{"type":"assistant","sessionId":"claude-native","message":{"role":"assistant","stop_reason":{{{(stop is null ? "null" : "\"" + stop + "\"")}}},"content":[{"type":"text","text":"{{{text}}}"}]}}""";
    const string ClaudeThinkingEnd = """{"type":"assistant","sessionId":"claude-native","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"thinking","thinking":"x"}]}}""";
    const string ClaudeToolResult = """{"type":"user","sessionId":"claude-native","message":{"role":"user","content":[{"type":"tool_result","content":"ok"}]}}""";
    const string ClaudeBackground = """{"type":"user","sessionId":"claude-native","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tool-1","content":"Running in background"}]},"toolUseResult":{"backgroundTaskId":"task-1"}}""";
    static string ClaudeNotification(string id = "task-1") => ClaudeUser($"<task-notification><task-id>{id}</task-id><status>completed</status></task-notification>");

    const string CodexMeta = """{"type":"session_meta","payload":{"id":"codex-native"}}""";
    const string CodexStarted = """{"type":"event_msg","payload":{"type":"task_started"}}""";
    const string CodexComplete = """{"type":"event_msg","payload":{"type":"task_complete"}}""";
    static string CodexUser(string text) =>
        $$$"""{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"{{{text}}}"}]}}""";
    static string CodexAssistant(string text) =>
        $$$"""{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"{{{text}}}"}]}}""";

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
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeBackground, ClaudeAssistant("waiting", "end_turn"), ClaudeNotification()], "waiting", false },
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeBackground, ClaudeAssistant("waiting", "end_turn"), ClaudeNotification(),
            ClaudeAssistant("DONE", "end_turn"), ClaudeUser("human asks more"), ClaudeAssistant("human reply", "end_turn")], "DONE", true },
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeBackground, ClaudeAssistant("waiting", "end_turn"), ClaudeUser("human takes over"),
            ClaudeNotification(), ClaudeAssistant("human reply", "end_turn")], "waiting", false },
        { InteractiveAgentKind.Claude, [ClaudeUser(Marker), ClaudeBackground, ClaudeAssistant("waiting", "end_turn"), ClaudeNotification("foreign-task"),
            ClaudeAssistant("human reply", "end_turn")], "waiting", false },
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
            """{"type":"user","sessionId":"claude-native","message":{"role":"user","content":[{"type":"image","source":{"type":"base64","data":"AA=="}}]}}""",
            ClaudeAssistant("human reply", "end_turn")], "interim", false },
        { InteractiveAgentKind.Pi, [PiHeader, PiUser(Marker), PiAssistant("interim", "toolUse"),
            """{"type":"message","message":{"role":"user","content":[{"type":"image","data":"AA==","mimeType":"image/png"}]}}""",
            PiAssistant("human reply", "stop")], "interim", false },
        { InteractiveAgentKind.Codex, [CodexMeta, CodexStarted, CodexUser(Marker), CodexAssistant("interim")], "interim", false },
        // Codex records the marked input twice (response_item + event_msg, either order); both are the same turn.
        { InteractiveAgentKind.Codex, [CodexMeta, CodexStarted, CodexUser(Marker), CodexEventUser(Marker), CodexAssistant("final"), CodexComplete], "final", true },
        { InteractiveAgentKind.Codex, [CodexMeta, CodexStarted, CodexEventUser(Marker), CodexUser(Marker), CodexAssistant("final"), CodexComplete], "final", true },
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

    public static TheoryData<InteractiveAgentKind, string[]> Unbound => new()
    {
        // Only a native user record binds the marker; echoes/metadata/partial lines never do.
        { InteractiveAgentKind.Claude, ["""{"type":"last-prompt","sessionId":"claude-native","lastPrompt":"atf-corr:turn-1"}""", ClaudeAssistant("stale", "end_turn")] },
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
