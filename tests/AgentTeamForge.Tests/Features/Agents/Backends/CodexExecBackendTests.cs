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
        // Codex's shell_environment_policy can drop the inherited run marker; the override puts it back in tool shells.
        Assert.Equal(["exec", "--json", "--dangerously-bypass-approvals-and-sandbox", "--skip-git-repo-check",
            "-c", "shell_environment_policy.set.ATF_RUN_CORRELATION=\"corr-1\"", "-"], Argv());
        Assert.Equal("do the thing", File.ReadAllText(_dir.File("stdin")));
        // `pwd` reports the physical path; on macOS /tmp is a symlink to /private/tmp.
        var cwd = File.ReadAllText(_dir.File("cwd")).Trim();
        Assert.EndsWith(Path.GetFileName(_dir.Path), cwd, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(cwd, "cwd")));
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
            ["exec", "resume", "--json", "--dangerously-bypass-approvals-and-sandbox", "--skip-git-repo-check",
                "-m", "gpt-x", "-c", "shell_environment_policy.set.ATF_RUN_CORRELATION=\"corr-2\"", ThreadId, "-"],
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
    public async Task Failed_turn_with_usage_limit_is_terminal_rate_limit_error()
    {
        var codex = FakeCodex(
            $$"""{"type":"thread.started","thread_id":"{{ThreadId}}"}""",
            """{"type":"turn.failed","error":{"message":"usage limit reached; resets at 2026-09-27T18:20:00Z"}}""");

        var evidence = await RunAsync(new CodexExecBackend(codex), new BackendRequest("job-limit", "corr-limit", "x", "") { WorkingDirectory = _dir.Path });

        Assert.Contains(new BackendEvidence.AgentError("agent_rate_limited", "usage limit reached; resets at 2026-09-27T18:20:00Z"), evidence);
    }

    [Fact]
    public async Task Native_usage_limit_stream_error_is_terminal_rate_limit_error()
    {
        const string limit = "You've hit your usage limit. Upgrade to Pro (https://openai.com/chatgpt/pricing) or try again at 3:05 PM.";
        var codex = FakeCodex($$"""{"type":"error","message":"{{limit}}"}""");

        var evidence = await RunAsync(new CodexExecBackend(codex), new BackendRequest("job-native-limit", "corr-native-limit", "x", "") { WorkingDirectory = _dir.Path });

        Assert.Equal([new BackendEvidence.AgentError("agent_rate_limited", limit)], evidence);
    }

    [Fact]
    public async Task Stream_error_without_completed_turn_is_a_protocol_error()
    {
        var codex = FakeCodex("""{"type":"error","message":"unauthorized"}""");

        var evidence = await RunAsync(new CodexExecBackend(codex), new BackendRequest("job-4", "corr-4", "x", "") { WorkingDirectory = _dir.Path });

        Assert.Equal([new BackendEvidence.ProtocolError("codex_error")], evidence);
    }

    [Fact]
    public async Task Completed_turn_with_only_tool_calls_has_empty_result()
    {
        var codex = FakeCodex(
            $$"""{"type":"thread.started","thread_id":"{{ThreadId}}"}""",
            """{"type":"item.completed","item":{"type":"command_execution","aggregated_output":"ok"}}""",
            """{"type":"turn.completed"}""");

        var evidence = await RunAsync(new CodexExecBackend(codex), new BackendRequest("job-empty", "corr-empty", "x", "") { WorkingDirectory = _dir.Path });

        Assert.Contains(new BackendEvidence.Result("corr-empty", ""), evidence);
        Assert.DoesNotContain(evidence, e => e is BackendEvidence.ProtocolError);
    }

    [Fact]
    public async Task Oversized_agent_message_cannot_complete_as_empty_result()
    {
        var codex = FakeCodex(
            $$"""{"type":"thread.started","thread_id":"{{ThreadId}}"}""",
            """{"type":"item.completed","item":{"type":"agent_message","text":""" + new string('x', CodexExecBackend.MaxLineBytes) + "\"}}",
            """{"type":"turn.completed"}""");

        var evidence = await RunAsync(new CodexExecBackend(codex), new BackendRequest("job-large", "corr-large", "x", "") { WorkingDirectory = _dir.Path });

        Assert.Contains(new BackendEvidence.ProtocolError("backend_malformed_output"), evidence);
        Assert.DoesNotContain(evidence, e => e is BackendEvidence.Result);
    }

    [Fact]
    public async Task Oversized_tool_output_is_skipped_before_final_message()
    {
        var codex = FakeCodex(
            $$"""{"type":"thread.started","thread_id":"{{ThreadId}}"}""",
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\",\"aggregated_output\":\"" + new string('x', CodexExecBackend.MaxLineBytes) + "\"}}",
            """{"type":"item.completed","item":{"type":"agent_message","text":"done"}}""",
            """{"type":"turn.completed"}""");

        var evidence = await RunAsync(new CodexExecBackend(codex), new BackendRequest("job-tool-large", "corr-tool-large", "x", ""));

        Assert.Contains(new BackendEvidence.Result("corr-tool-large", "done"), evidence);
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

        await Bounded.Until(() => GrandchildExited(grandchild), "grandchild exit");
    }

    [Fact]
    public async Task Terminating_an_already_exited_child_succeeds()
    {
        var script = _dir.File("codex-exit");
        File.WriteAllText(script, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using var run = new CodexExecBackend(script).Start(new BackendRequest("job-exit", "corr-exit", "x", ""));
        await foreach (var _ in run.ReadEvidenceAsync(TestContext.Current.CancellationToken)) { }

        run.TerminateOwnedChild();
    }

    static bool GrandchildExited(int pid)
    {
        if (!OperatingSystem.IsLinux())
        {
            // No /proc: a missing stat file would pass without proving anything.
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        var stat = $"/proc/{pid}/stat";
        try
        {
            return !File.Exists(stat) || File.ReadAllText(stat).Contains(") Z ", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            // /proc can disappear between the existence check and the read.
            return !File.Exists(stat);
        }
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

    // Captured from a signed-out `codex exec --json` (HOME without credentials).
    const string MissingCredential = "unexpected status 401 Unauthorized: Missing bearer or basic authentication in header, url: https://api.openai.com/v1/responses, cf-ray: a43bd43bdcb72d94-PRG";

    [Fact]
    public async Task Missing_credential_ends_the_turn_as_login_required_without_waiting_for_retries()
    {
        File.WriteAllLines(_dir.File("out.jsonl"),
        [
            $$"""{"type":"thread.started","thread_id":"{{ThreadId}}"}""",
            """{"type":"turn.started"}""",
            $$"""{"type":"error","message":"Reconnecting... 2/5 ({{MissingCredential}})"}""",
        ]);
        var script = _dir.File("codex-signed-out");
        File.WriteAllText(script, $"""
            #!/usr/bin/env bash
            input="$(cat)"
            cat '{_dir.File("out.jsonl")}'
            # Signed-out codex keeps retrying; the executable probe sends no input and exits.
            [ -n "$input" ] && exec sleep 300

            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        WaitUntilExecutable(script);
        List<string> log = [];
        var request = new BackendRequest("job-login", "corr-login", "x", "")
        {
            WorkingDirectory = _dir.Path,
            Output = (stream, bytes) => { lock (log) { log.Add(stream + ":" + System.Text.Encoding.UTF8.GetString(bytes.Span)); } },
        };
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var evidence = await RunAsync(new CodexExecBackend(script), request, TimeSpan.FromSeconds(30));

        // Disposal waits 5 s for a child that was not terminated.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"took {clock.Elapsed}");
        Assert.Equal(new BackendEvidence.Session("corr-login", ThreadId), evidence[1]);
        var login = Assert.IsType<BackendEvidence.AgentError>(evidence[^1]);
        Assert.Equal("agent_login_required", login.Code);
        Assert.Contains("codex login", login.Details, StringComparison.Ordinal);
        Assert.Contains(log, line => line.StartsWith("status:Codex is not logged in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failed_turn_with_401_is_login_required()
    {
        var codex = FakeCodex(
            $$"""{"type":"thread.started","thread_id":"{{ThreadId}}"}""",
            """{"type":"error","message":"unexpected status 401 Unauthorized: token expired"}""",
            """{"type":"turn.failed","error":{"message":"unexpected status 401 Unauthorized: token expired"}}""");

        var evidence = await RunAsync(new CodexExecBackend(codex), new BackendRequest("job-401", "corr-401", "x", "") { WorkingDirectory = _dir.Path });

        Assert.Equal("agent_login_required", Assert.IsType<BackendEvidence.AgentError>(evidence[^1]).Code);
        Assert.Equal("agent_login_required", Assert.IsType<BackendEvidence.AgentError>(
            Assert.Single(new CodexEventParser("c").Parse(System.Text.Encoding.UTF8.GetBytes(
                $$$"""{"type":"turn.failed","error":{"message":"{{{MissingCredential}}}"}}""")))).Code);
    }

    [Fact]
    public async Task Reply_that_quotes_a_401_still_completes()
    {
        var codex = FakeCodex(
            $$"""{"type":"thread.started","thread_id":"{{ThreadId}}"}""",
            $$$"""{"type":"item.completed","item":{"type":"agent_message","text":"The log said: {{{MissingCredential}}}. Run codex login."}}""",
            """{"type":"turn.completed"}""");

        var evidence = await RunAsync(new CodexExecBackend(codex), new BackendRequest("job-quote", "corr-quote", "x", "") { WorkingDirectory = _dir.Path });

        Assert.Contains(evidence, e => e is BackendEvidence.Result { Output: var text } && text.Contains("401", StringComparison.Ordinal));
        Assert.DoesNotContain(evidence, e => e is BackendEvidence.AgentError);
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
        WaitUntilExecutable(script);
        return script;
    }

    // A parallel test's fork can briefly inherit our write fd, so exec fails with ETXTBSY (26) even though the
    // file is closed here. Probe with a throwaway exec until the window has passed; the real run then never sees it.
    static void WaitUntilExecutable(string script)
    {
        static void Probe(string script)
        {
            using var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(script)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            })!;
            probe.StandardInput.Close();
            probe.StandardOutput.ReadToEnd();
            probe.WaitForExit();
        }

        for (var i = 0; i < 100; i++)
        {
            try
            {
                Probe(script);
                return;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 26)
            {
                Thread.Sleep(20);
            }
        }
        Probe(script);
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
