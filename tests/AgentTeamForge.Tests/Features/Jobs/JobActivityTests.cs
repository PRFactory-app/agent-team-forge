using System.Text;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class JobActivityTests
{
    [Fact]
    public void Jsonl_fixtures_normalize_known_events_and_skip_unknown_events()
    {
        var claude = JobActivity.Normalize("claude", """
            {"type":"assistant","message":{"content":[{"type":"text","text":"I will check"},{"type":"tool_use","name":"Bash","input":{"command":"pwd"}}]}}
            """);
        Assert.Equal(["assistant_text", "tool_call"], claude.Select(x => x.Kind));
        Assert.Equal("I will check", claude[0].Text);
        Assert.Equal("Bash", claude[1].Text);
        Assert.Equal("tool_result", Assert.Single(JobActivity.Normalize("claude", """{"type":"user","message":{"content":[{"type":"tool_result","content":"done"}]}}""")).Kind);
        Assert.Equal("result", Assert.Single(JobActivity.Normalize("claude", """{"type":"result","is_error":false,"result":"finished"}""")).Kind);

        Assert.Equal("tool_call", Assert.Single(JobActivity.Normalize("codex", """{"type":"item.started","item":{"type":"command_execution","command":"pwd"}}""")).Kind);
        Assert.Equal("tool_result", Assert.Single(JobActivity.Normalize("codex", """{"type":"item.completed","item":{"type":"command_execution","aggregated_output":"/tmp"}}""")).Kind);
        Assert.Equal("assistant_text", Assert.Single(JobActivity.Normalize("codex", """{"type":"item.completed","item":{"type":"agent_message","text":"hello"}}""")).Kind);

        Assert.Equal("assistant_text", Assert.Single(JobActivity.Normalize("pi", """{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"hi"}]}}""")).Kind);
        Assert.Equal("tool_call", Assert.Single(JobActivity.Normalize("pi", """{"type":"tool_execution_start","toolName":"bash"}""")).Kind);
        Assert.Empty(JobActivity.Normalize("pi", """{"type":"future_event","value":1}"""));
        Assert.Empty(JobActivity.Normalize("claude", "not json"));
        Assert.Equal("assistant_text", Assert.Single(JobActivity.Normalize("plain", "terminal transcript")).Kind);
        Assert.Equal(JobActivity.MaxTextChars + 1, Assert.Single(JobActivity.Normalize("plain", new string('x', 1000))).Text.Length);
    }

    [Theory]
    [InlineData("claude", """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"hmm"}]}}""")]
    [InlineData("pi", """{"type":"message_end","message":{"role":"assistant","content":[{"type":"thinking","thinking":"hmm"}]}}""")]
    [InlineData("codex", """{"type":"item.completed","item":{"type":"reasoning","text":"hmm"}}""")]
    public void Thinking_is_its_own_activity_kind_and_empty_thinking_is_skipped(string backend, string line)
    {
        var entry = Assert.Single(JobActivity.Normalize(backend, line));
        Assert.Equal(("thinking", "hmm"), (entry.Kind, entry.Text));
        Assert.Empty(JobActivity.Normalize(backend, line.Replace("hmm", "")));
    }

    [Fact]
    public void Cursor_pages_only_complete_lines_and_resumes_after_new_output()
    {
        using var state = new TempStateDir();
        var logs = new JobLogs(state.Path);
        var write = logs.BeginRun("job-1", "run-1", "codex");
        write("stdout", Encoding.UTF8.GetBytes("""{"type":"item.completed","item":{"type":"agent_message","text":"one"}}""" + "\n"));
        write("stdout", Encoding.UTF8.GetBytes("""{"type":"item.completed","item":{"type":"agent_message","text":"two"}}""" + "\n"));
        var first = logs.ReadActivity("job-1", "codex", limit: 1);
        Assert.Equal("one", Assert.Single(first.Entries).Text);
        var second = logs.ReadActivity("job-1", "codex", first.NextCursor, 1);
        Assert.Equal("two", Assert.Single(second.Entries).Text);
        Assert.True(second.NextCursor > first.NextCursor);
        Assert.Equal("two", logs.LastActivity("job-1", "codex"));

        write("stdout", Encoding.UTF8.GetBytes("""{"type":"item.completed","item":{"type":"agent_message","text":"three"}}"""));
        Assert.Empty(logs.ReadActivity("job-1", "codex", second.NextCursor).Entries);
        write("stdout", "\n"u8.ToArray());
        Assert.Equal("three", Assert.Single(logs.ReadActivity("job-1", "codex", second.NextCursor).Entries).Text);
        write("stdout", Encoding.UTF8.GetBytes("""{"type":"item.completed","item":{"type":"agent_message","text":"line1\nline2"}}""" + "\n"));
        Assert.Equal("line1 line2", logs.LastActivity("job-1", "codex"));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("pi")]
    public void Stderr_is_visible_and_interleaved_stdout_json_stays_intact(string backend)
    {
        using var state = new TempStateDir();
        var logs = new JobLogs(state.Path);
        var write = logs.BeginRun("job", "run", backend);
        var json = backend switch
        {
            "claude" => """{"type":"result","result":"done"}""",
            "codex" => """{"type":"item.completed","item":{"type":"agent_message","text":"done"}}""",
            _ => """{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"done"}]}}""",
        };
        var split = json.Length / 2;
        write("stdout", Encoding.UTF8.GetBytes(json[..split]));
        write("stderr", "authentication failed\n"u8.ToArray());
        write("stdout", Encoding.UTF8.GetBytes(json[split..] + "\n"));
        write("stderr", "partial diagnostic"u8.ToArray());
        write("stderr", ReadOnlyMemory<byte>.Empty);

        var entries = logs.ReadActivity("job", backend).Entries;
        Assert.Equal(["error", backend == "claude" ? "result" : "assistant_text", "error"], entries.Select(e => e.Kind));
        Assert.Equal(["authentication failed", "done", "partial diagnostic"], entries.Select(e => e.Text));
        var raw = logs.Read("job").Text;
        Assert.Contains(json + "\n", raw);
        Assert.DoesNotContain(json[..split] + "\n[stderr]", raw);
    }

    [Fact]
    public void Cursor_taken_between_tag_and_line_keeps_the_stream()
    {
        using var state = new TempStateDir();
        var logs = new JobLogs(state.Path);
        logs.BeginRun("job", "run", "codex");
        var path = System.IO.Path.Combine(state.Path, "logs", "job.log");
        File.AppendAllText(path, "[stderr]\n");

        var cursor = logs.ReadActivity("job", "codex").NextCursor;
        File.AppendAllText(path, "boom\n");
        var next = logs.ReadActivity("job", "codex", cursor).Entries;
        Assert.Equal("error", Assert.Single(next).Kind);
    }

    [Fact]
    public void Interactive_transcript_lines_carry_their_native_time_and_undated_ones_none()
    {
        var dated = new InteractiveTranscript("s", "hi", ["hi", "plain"], Times: ["2026-09-30T10:00:00Z", null]);
        var entry = Assert.Single(JobActivity.Normalize("plain", dated.ProgressLine(0)));
        Assert.Equal(("2026-09-30T10:00:00Z", "assistant_text", "hi"), (entry.Ts, entry.Kind, entry.Text));
        var undated = Assert.Single(JobActivity.Normalize("plain", dated.ProgressLine(1)));
        Assert.Equal(("", "plain"), (undated.Ts, undated.Text));
    }
}
