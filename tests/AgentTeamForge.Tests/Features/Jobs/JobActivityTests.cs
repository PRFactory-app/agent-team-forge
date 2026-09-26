using System.Text;
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
}
