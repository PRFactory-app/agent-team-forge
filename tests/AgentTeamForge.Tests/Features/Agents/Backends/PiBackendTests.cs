using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Backends;

public sealed class PiBackendTests : IDisposable
{
    // Mimics `pi -p --mode json`: records argv in its cwd, echoes stdin as the
    // assistant answer, and switches behaviour on --model.
    const string FakePi = """
        #!/usr/bin/env bash
        printf '%s\n' "$@" > argv.txt
        sid=""; model=""
        while [ $# -gt 0 ]; do
          case "$1" in
            --session-id|--session) sid="$2"; shift ;;
            --model) model="$2"; shift ;;
          esac
          shift
        done
        input="$(cat)"
        echo '{"type":"session","version":3,"id":"'"$sid"'"}'
        echo 'not json noise'
        echo '{"type":"agent_start"}'
        if [ "$model" = hang ]; then sleep 300 & echo $! > child.pid; wait; fi
        echo '{"type":"message_end","message":{"role":"user","content":[{"type":"text","text":"ignored"}]}}'
        if [ "$model" = error ]; then
          echo '{"type":"message_end","message":{"role":"assistant","content":[],"stopReason":"error","errorMessage":"boom"}}'
        else
          echo '{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"echo:'"$input"'"},{"type":"thinking","thinking":"x"},{"type":"text","text":"@'"$(basename "$PWD")"'"}],"stopReason":"stop"}}'
        fi
        echo '{"type":"agent_end","messages":[],"willRetry":false}'
        echo '{"type":"agent_settled"}'
        """;

    readonly TempStateDir _dir = new();
    readonly PiBackend _backend;

    public PiBackendTests()
    {
        var script = _dir.File("pi");
        File.WriteAllText(script, FakePi.ReplaceLineEndings("\n") + "\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _backend = new PiBackend(script);
    }

    [Fact]
    public async Task New_session_reports_session_ack_result_in_working_directory()
    {
        var evidence = await RunAsync(Request("hello"));

        var session = Assert.IsType<BackendEvidence.Session>(evidence[0]);
        Assert.True(Guid.TryParse(session.SessionId, out _));
        Assert.Equal(
            [
                session,
                new BackendEvidence.Ack("c1"),
                new BackendEvidence.Result("c1", "echo:hello@" + Path.GetFileName(_dir.Path)),
                new BackendEvidence.EndOfOutput(),
            ],
            evidence);
        var argv = File.ReadAllLines(_dir.File("argv.txt"));
        Assert.Equal(["-p", "--mode", "json"], argv[..3]);
        Assert.Contains("--session-id", argv);
        Assert.DoesNotContain("--session", argv);
        Assert.DoesNotContain("hello", argv);
    }

    [Fact]
    public async Task Resume_passes_existing_session_and_model_options()
    {
        var evidence = await RunAsync(Request("again", "model=openai-codex/gpt-6-luna;thinking=low") with { ResumeSessionId = "sess-42" });

        Assert.Equal(new BackendEvidence.Session("c1", "sess-42"), evidence[0]);
        Assert.IsType<BackendEvidence.Result>(evidence[2]);
        var argv = File.ReadAllLines(_dir.File("argv.txt"));
        Assert.Equal(["--session", "sess-42"], argv.SkipWhile(a => a != "--session").Take(2));
        Assert.Equal(["--model", "openai-codex/gpt-6-luna"], argv.SkipWhile(a => a != "--model").Take(2));
        Assert.Equal(["--thinking", "low"], argv.SkipWhile(a => a != "--thinking").Take(2));
        Assert.DoesNotContain("--session-id", argv);
    }

    [Fact]
    public async Task Assistant_error_is_a_protocol_error()
    {
        var evidence = await RunAsync(Request("x", "model=error"));

        Assert.Equal(new BackendEvidence.ProtocolError("pi_error"), evidence[^2]);
        Assert.DoesNotContain(evidence, e => e is BackendEvidence.Result);
    }

    [Fact]
    public void Assistant_message_without_agent_settled_is_not_a_result()
    {
        var turn = new PiBackend.TurnState();
        var message = System.Text.Encoding.UTF8.GetBytes("""
            {"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"partial"}],"stopReason":"stop"}}
            """);

        Assert.Empty(turn.Observe(message, "c1"));
        Assert.Equal(new BackendEvidence.ProtocolError("pi_not_settled"), turn.FinishAtEndOfOutput());
    }

    [Fact]
    public async Task Oversized_tool_event_does_not_hide_final_answer()
    {
        var output = _dir.File("large.jsonl");
        File.WriteAllLines(output,
        [
            """{"type":"session","id":"s1"}""",
            """{"type":"agent_start"}""",
            "{\"type\":\"tool_execution_end\",\"result\":\"" + new string('x', PiBackend.MaxLineBytes) + "\"}",
            """{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"done"}]}}""",
            """{"type":"agent_settled"}""",
        ]);
        var script = _dir.File("pi-large");
        File.WriteAllText(script, "#!/usr/bin/env bash\ncat >/dev/null\ncat '" + output + "'\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var evidence = await RunAsync(Request("x"), new PiBackend(script));

        Assert.Contains(new BackendEvidence.Result("c1", "done"), evidence);
    }

    [Fact]
    public async Task Terminate_kills_the_owned_process_tree()
    {
        await using var run = _backend.Start(Request("x", "model=hang"));
        await run.DeliverAsync(CancellationToken.None);
        await Bounded.Until(() => File.Exists(_dir.File("child.pid")) && File.ReadAllText(_dir.File("child.pid")).Trim().Length > 0, "grandchild pid");
        var grandchild = int.Parse(File.ReadAllText(_dir.File("child.pid")).Trim());

        run.TerminateOwnedChild();

        await Bounded.Until(() => !IsAlive(grandchild), "grandchild exit");
    }

    [Fact]
    public void Missing_executable_is_not_started()
    {
        var backend = new PiBackend(_dir.File("no-such-pi"));

        Assert.Throws<BackendNotStartedException>(() => backend.Start(Request("x")));
    }

    [Fact]
    public async Task Real_pi_new_and_resumed_session()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ATF_REAL_PI") == "1", "set ATF_REAL_PI=1 to run against the installed pi");
        var backend = new PiBackend();
        var options = "model=openai-codex/gpt-6-luna;thinking=low";

        var first = await RunAsync(new BackendRequest("j1", "c1", "Remember the word KUMQUAT. Reply with exactly: OK", options) { WorkingDirectory = _dir.Path }, backend);
        var session = Assert.Single(first.OfType<BackendEvidence.Session>());
        Assert.Contains(first, e => e is BackendEvidence.Result);

        var second = await RunAsync(new BackendRequest("j2", "c2", "Which word did I ask you to remember? Reply with the word only.", options) { WorkingDirectory = _dir.Path, ResumeSessionId = session.SessionId }, backend);
        var result = Assert.Single(second.OfType<BackendEvidence.Result>());
        Assert.Contains("KUMQUAT", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    BackendRequest Request(string instruction, string options = "") =>
        new("j1", "c1", instruction, options) { WorkingDirectory = _dir.Path };

    async Task<List<BackendEvidence>> RunAsync(BackendRequest request, PiBackend? backend = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var run = (backend ?? _backend).Start(request);
        await run.DeliverAsync(timeout.Token);
        List<BackendEvidence> evidence = [];
        await foreach (var item in run.ReadEvidenceAsync(timeout.Token))
        {
            evidence.Add(item);
        }

        return evidence;
    }

    static bool IsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void Dispose() => _dir.Dispose();
}
