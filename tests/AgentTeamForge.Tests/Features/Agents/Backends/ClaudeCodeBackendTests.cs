using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Backends;

public sealed class ClaudeCodeBackendTests : IDisposable
{
    readonly TempStateDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>Fake claude: records argv, cwd and stdin, then prints canned JSON.</summary>
    string FakeClaude(string json)
    {
        // A previous invocation may still have this script open when its next
        // test turn starts. Linux refuses an in-place rewrite with ETXTBSY.
        var script = _dir.File("claude-" + Guid.NewGuid().ToString("N"));
        var reply = script + ".reply.json";
        File.WriteAllText(reply, json);
        File.WriteAllText(script, $"""
            #!/usr/bin/env bash
            printf '%s\n' "$@" > '{_dir.File("argv")}'
            pwd > '{_dir.File("cwd")}'
            cat > '{_dir.File("stdin")}'
            cat '{reply}'
            """.ReplaceLineEndings("\n"));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    static async Task<List<BackendEvidence>> RunAsync(ClaudeCodeBackend backend, BackendRequest request)
    {
        await using var run = backend.Start(request);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await run.DeliverAsync(timeout.Token);
        List<BackendEvidence> evidence = [];
        await foreach (var item in run.ReadEvidenceAsync(timeout.Token))
        {
            evidence.Add(item);
        }

        return evidence;
    }

    [Fact]
    public async Task New_session_delivers_instruction_on_stdin_and_reports_session_then_result()
    {
        var backend = new ClaudeCodeBackend(FakeClaude("""{"type":"result","subtype":"success","is_error":false,"result":"hello","session_id":"s-1"}"""));

        var evidence = await RunAsync(backend, new BackendRequest("j1", "c1", "say --hi", "") { WorkingDirectory = _dir.Path });

        // The session id is chosen up front and reported before any output.
        var argv = File.ReadAllLines(_dir.File("argv"));
        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose", "--dangerously-skip-permissions", "--session-id"], argv[..^1]);
        var chosen = argv[^1];
        Assert.True(Guid.TryParse(chosen, out _));
        Assert.Equal<BackendEvidence>(
            [new BackendEvidence.Session("c1", chosen), new BackendEvidence.Ack("c1"), new BackendEvidence.Session("c1", "s-1"), new BackendEvidence.Result("c1", "hello"), new BackendEvidence.EndOfOutput()],
            evidence);
        Assert.Equal("say --hi", File.ReadAllText(_dir.File("stdin")));
        Assert.Equal(_dir.Path, File.ReadAllText(_dir.File("cwd")).Trim());
    }

    [Fact]
    public async Task Follow_up_passes_resume_session()
    {
        var backend = new ClaudeCodeBackend(FakeClaude("""{"type":"result","subtype":"success","is_error":false,"result":"again","session_id":"s-1"}"""));

        var evidence = await RunAsync(backend, new BackendRequest("j2", "c2", "more", "") { ResumeSessionId = "s-1" });

        Assert.Contains(new BackendEvidence.Result("c2", "again"), evidence);
        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose", "--dangerously-skip-permissions", "--resume", "s-1"], File.ReadAllLines(_dir.File("argv")));
    }

    [Fact]
    public async Task Stream_events_before_final_result_preserve_session_and_error_parsing()
    {
        var backend = new ClaudeCodeBackend(FakeClaude("""
            {"type":"system","subtype":"init","session_id":"s-1"}
            {"type":"assistant","message":{"content":[{"type":"text","text":"working"}]}}
            {"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"s-1"}
            """));
        var evidence = await RunAsync(backend, new BackendRequest("j-stream", "c-stream", "work", ""));
        Assert.Contains(new BackendEvidence.Result("c-stream", "done"), evidence);
        Assert.Contains(new BackendEvidence.Session("c-stream", "s-1"), evidence);

        backend = new ClaudeCodeBackend(FakeClaude("""
            {"type":"assistant","message":{"content":[{"type":"text","text":"partial"}]}}
            {"type":"result","subtype":"error_max_turns","is_error":true,"session_id":"s-2"}
            """));
        evidence = await RunAsync(backend, new BackendRequest("j-error", "c-error", "work", ""));
        Assert.Contains(new BackendEvidence.Session("c-error", "s-2"), evidence);
        Assert.Contains(new BackendEvidence.ProtocolError("claude_error:error_max_turns"), evidence);
    }

    [Fact]
    public async Task Resumed_result_mentioning_a_missing_session_is_not_session_expired()
    {
        var backend = new ClaudeCodeBackend(FakeClaude("""{"type":"result","subtype":"success","is_error":false,"result":"fixed the session not found bug","session_id":"s-1"}"""));

        var evidence = await RunAsync(backend, new BackendRequest("j3", "c3", "more", "") { ResumeSessionId = "s-1" });

        Assert.Contains(new BackendEvidence.Result("c3", "fixed the session not found bug"), evidence);
        Assert.DoesNotContain(evidence, e => e is BackendEvidence.ProtocolError);
    }

    [Fact]
    public async Task Status_line_from_command_shim_does_not_hide_claude_result()
    {
        var backend = new ClaudeCodeBackend(FakeClaude("""
            mise ~/.config/mise/config.toml tools: claude@2.1.283
            {"type":"result","subtype":"success","is_error":false,"result":"hello","session_id":"s-1"}
            """));

        var evidence = await RunAsync(backend, new BackendRequest("j1", "c1", "hello", ""));

        Assert.Contains(new BackendEvidence.Session("c1", "s-1"), evidence);
        Assert.Contains(new BackendEvidence.Result("c1", "hello"), evidence);
    }

    [Fact]
    public async Task Oversized_non_result_line_does_not_hide_final_result()
    {
        var output = new List<string>();
        var backend = new ClaudeCodeBackend(FakeClaude(
            "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\""
            + new string('x', ClaudeCodeBackend.MaxOutputBytes) + "\"}]}}\n"
            + """{"type":"result","subtype":"success","is_error":false,"result":"done"}"""));

        var evidence = await RunAsync(backend, new BackendRequest("j-overflow", "c-overflow", "x", "")
        {
            Output = (stream, bytes) =>
            {
                if (stream == "status")
                {
                    output.Add(System.Text.Encoding.UTF8.GetString(bytes.Span));
                }
            },
        });

        Assert.Contains(new BackendEvidence.Result("c-overflow", "done"), evidence);
        Assert.Contains(output, line => line.Contains("omitted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Oversized_final_result_is_not_accepted()
    {
        var backend = new ClaudeCodeBackend(FakeClaude(
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\""
            + new string('x', ClaudeCodeBackend.MaxOutputBytes) + "\"}"));

        var evidence = await RunAsync(backend, new BackendRequest("j-overflow-result", "c-overflow-result", "x", ""));

        Assert.Contains(new BackendEvidence.ProtocolError("backend_malformed_output"), evidence);
        Assert.DoesNotContain(evidence, e => e is BackendEvidence.Result);
    }

    [Theory]
    [InlineData("""{"type":"result","subtype":"error_max_turns","is_error":true,"session_id":"s-9"}""")]
    [InlineData("not json")]
    public async Task Error_or_malformed_output_is_a_protocol_error(string json)
    {
        var backend = new ClaudeCodeBackend(FakeClaude(json));

        var evidence = await RunAsync(backend, new BackendRequest("j3", "c3", "x", ""));

        Assert.Single(evidence.OfType<BackendEvidence.ProtocolError>());
        Assert.DoesNotContain(evidence, e => e is BackendEvidence.Result);
    }

    [Fact]
    public void Missing_executable_is_not_started()
    {
        var backend = new ClaudeCodeBackend(_dir.File("no-such-claude"));

        Assert.Throws<BackendNotStartedException>(() => backend.Start(new BackendRequest("j4", "c4", "x", "")));
    }

    /// <summary>Opt-in: ATF_REAL_CLAUDE=1 runs one tiny prompt through the real claude CLI.</summary>
    [Fact]
    public async Task Real_claude_answers_and_resumes()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ATF_REAL_CLAUDE") == "1", "set ATF_REAL_CLAUDE=1 to run");
        var backend = new ClaudeCodeBackend();

        var first = await RunAsync(backend, new BackendRequest("r1", "c1", "Reply with exactly the word PONG.", "") { WorkingDirectory = _dir.Path });
        var sessions = first.OfType<BackendEvidence.Session>().ToArray();
        Assert.True(sessions.Length == 1, $"Evidence: {string.Join(", ", first)}");
        var session = sessions[0].SessionId;
        Assert.Contains("PONG", Assert.Single(first.OfType<BackendEvidence.Result>()).Output, StringComparison.OrdinalIgnoreCase);

        var second = await RunAsync(backend, new BackendRequest("r2", "c2", "What word did you just reply with? Answer with only that word.", "") { ResumeSessionId = session, WorkingDirectory = _dir.Path });
        Assert.Contains("PONG", Assert.Single(second.OfType<BackendEvidence.Result>()).Output, StringComparison.OrdinalIgnoreCase);
    }
}
