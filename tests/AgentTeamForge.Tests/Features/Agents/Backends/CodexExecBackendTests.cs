using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Backends;

public sealed class CodexExecBackendTests : IDisposable
{
    const string ThreadId = "0199a213-81c0-7800-8aa1-bbab2a035a53";
    readonly TempStateDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task New_session_passes_instruction_on_stdin_and_maps_events()
    {
        var codex = FakeCodex(
            $$"""{"type":"thread.started","thread_id":"{{ThreadId}}"}""",
            """{"type":"turn.started"}""",
            "not json diagnostics",
            """{"type":"item.completed","item":{"id":"item_0","type":"reasoning","text":"thinking"}}""",
            """{"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"first"}}""",
            """{"type":"item.completed","item":{"id":"item_2","type":"agent_message","text":"final answer"}}""",
            """{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":2}}""");
        var request = new BackendRequest("job-1", "corr-1", "do the thing", "") { WorkingDirectory = _dir.Path };

        var evidence = await RunAsync(new CodexExecBackend(codex), request);

        Assert.Equal(
            [
                new BackendEvidence.Ack("corr-1"),
                new BackendEvidence.Session("corr-1", ThreadId),
                new BackendEvidence.Result("corr-1", "final answer"),
                new BackendEvidence.EndOfOutput(),
            ],
            evidence);
        Assert.Equal(["exec", "--json", "--dangerously-bypass-approvals-and-sandbox", "--skip-git-repo-check", "-"], Argv());
        Assert.Equal("do the thing", File.ReadAllText(_dir.File("stdin")));
        Assert.Equal(_dir.Path, File.ReadAllText(_dir.File("cwd")).Trim());
    }

    [Fact]
    public async Task Follow_up_resumes_the_native_session()
    {
        var codex = FakeCodex(
            $$"""{"type":"thread.started","thread_id":"{{ThreadId}}"}""",
            """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"again"}}""",
            """{"type":"turn.completed","usage":{}}""");
        var request = new BackendRequest("job-2", "corr-2", "continue", "model=gpt-x") { ResumeSessionId = ThreadId, WorkingDirectory = _dir.Path };

        var evidence = await RunAsync(new CodexExecBackend(codex), request);

        Assert.Contains(new BackendEvidence.Result("corr-2", "again"), evidence);
        Assert.Equal(
            ["exec", "resume", "--json", "--dangerously-bypass-approvals-and-sandbox", "--skip-git-repo-check", "-m", "gpt-x", ThreadId, "-"],
            Argv());
    }

    [Fact]
    public async Task Failed_turn_is_a_protocol_error()
    {
        var codex = FakeCodex(
            $$"""{"type":"thread.started","thread_id":"{{ThreadId}}"}""",
            """{"type":"error","message":"stream disconnected"}""",
            """{"type":"turn.failed","error":{"message":"stream disconnected"}}""");

        var evidence = await RunAsync(new CodexExecBackend(codex), new BackendRequest("job-3", "corr-3", "x", "") { WorkingDirectory = _dir.Path });

        Assert.Equal(new BackendEvidence.ProtocolError("codex_turn_failed"), evidence[^1]);
        Assert.DoesNotContain(evidence, e => e is BackendEvidence.Result);
    }

    [Fact]
    public async Task Stream_error_without_completed_turn_is_a_protocol_error()
    {
        var codex = FakeCodex("""{"type":"error","message":"unauthorized"}""");

        var evidence = await RunAsync(new CodexExecBackend(codex), new BackendRequest("job-4", "corr-4", "x", "") { WorkingDirectory = _dir.Path });

        Assert.Equal([new BackendEvidence.ProtocolError("codex_error")], evidence);
    }

    [Fact]
    public async Task Oversized_agent_message_cannot_complete_as_empty_result()
    {
        var codex = FakeCodex(
            $$"""{"type":"thread.started","thread_id":"{{ThreadId}}"}""",
            """{"type":"item.completed","item":{"type":"agent_message","text":""" + new string('x', CodexExecBackend.MaxLineBytes) + "\"}}",
            """{"type":"turn.completed"}""");

        var evidence = await RunAsync(new CodexExecBackend(codex), new BackendRequest("job-large", "corr-large", "x", "") { WorkingDirectory = _dir.Path });

        Assert.Equal(new BackendEvidence.ProtocolError("backend_line_too_long"), evidence[^1]);
        Assert.DoesNotContain(evidence, e => e is BackendEvidence.Result);
    }

    [Fact]
    public void Missing_executable_never_starts()
    {
        var backend = new CodexExecBackend(_dir.File("no-such-codex"));

        Assert.Throws<BackendNotStartedException>(() => backend.Start(new BackendRequest("job-5", "corr-5", "x", "")));
    }

    [Fact]
    public async Task Terminate_kills_the_owned_process_tree()
    {
        var script = _dir.File("codex-hang");
        await File.WriteAllTextAsync(script, $"#!/usr/bin/env bash\nsleep 300 &\necho $! > '{_dir.File("grandchild")}'\nwait\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using var run = new CodexExecBackend(script).Start(new BackendRequest("job-6", "corr-6", "x", ""));
        await Bounded.Until(() => File.Exists(_dir.File("grandchild")) && File.ReadAllText(_dir.File("grandchild")).Trim().Length > 0, "grandchild pid");
        var grandchild = int.Parse(File.ReadAllText(_dir.File("grandchild")).Trim(), System.Globalization.CultureInfo.InvariantCulture);

        run.TerminateOwnedChild();

        await Bounded.Until(() => !Directory.Exists($"/proc/{grandchild}") || File.ReadAllText($"/proc/{grandchild}/stat").Contains(") Z ", StringComparison.Ordinal), "grandchild exit");
    }

    [Fact]
    public async Task Real_codex_answers_and_resumes()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ATF_REAL_CODEX") == "1", "set ATF_REAL_CODEX=1 to run against the real codex CLI");
        var backend = new CodexExecBackend();

        var first = await RunAsync(backend, new BackendRequest("real-1", "c1", "Reply with exactly the word PONG and nothing else. Do not run commands.", "") { WorkingDirectory = _dir.Path }, TimeSpan.FromMinutes(5));
        var session = Assert.Single(first.OfType<BackendEvidence.Session>());
        Assert.Contains("PONG", Assert.Single(first.OfType<BackendEvidence.Result>()).Output, StringComparison.Ordinal);

        var second = await RunAsync(backend, new BackendRequest("real-2", "c2", "What word did you just reply with? Answer with that word only.", "") { ResumeSessionId = session.SessionId, WorkingDirectory = _dir.Path }, TimeSpan.FromMinutes(5));
        Assert.Contains("PONG", Assert.Single(second.OfType<BackendEvidence.Result>()).Output, StringComparison.Ordinal);
    }

    string FakeCodex(params string[] lines)
    {
        File.WriteAllLines(_dir.File("out.jsonl"), lines);
        var script = _dir.File("codex");
        File.WriteAllText(script, $"""
            #!/usr/bin/env bash
            printf '%s\n' "$@" > '{_dir.File("argv")}'
            pwd > '{_dir.File("cwd")}'
            cat > '{_dir.File("stdin")}'
            cat '{_dir.File("out.jsonl")}'

            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    string[] Argv() => File.ReadAllLines(_dir.File("argv"));

    static async Task<List<BackendEvidence>> RunAsync(CodexExecBackend backend, BackendRequest request, TimeSpan? deadline = null)
    {
        using var cts = new CancellationTokenSource(deadline ?? Bounded.ScenarioDeadline);
        await using var run = backend.Start(request);
        await run.DeliverAsync(cts.Token);
        var evidence = new List<BackendEvidence>();
        await foreach (var item in run.ReadEvidenceAsync(cts.Token))
        {
            evidence.Add(item);
        }

        return evidence;
    }
}
