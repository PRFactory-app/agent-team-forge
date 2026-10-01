using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Backends;

public sealed class CursorDroidBackendTests : IDisposable
{
    readonly TempStateDir _state = new();

    public void Dispose() => _state.Dispose();

    [Theory]
    [InlineData("cursor")]
    [InlineData("droid")]
    public async Task Json_result_binds_session_and_follow_up_uses_same_session(string backend)
    {
        if (!OperatingSystem.IsLinux()) { return; }
        var script = FakeCli("""{"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"session-1"}""");
        IJobBackend agent = backend == "cursor" ? new CursorCliBackend(script) : new DroidBackend(script);
        var first = new BackendRequest("job", "corr", "first\nline", "model=test-model;effort=high") { WorkingDirectory = _state.Path };
        var firstEvidence = await Run(agent, first);
        Assert.Equal([new BackendEvidence.Ack("corr"), new BackendEvidence.Session("corr", "session-1"),
            new BackendEvidence.Result("corr", "done")], firstEvidence);
        Assert.Equal("first\nline", File.ReadAllText(_state.File("stdin")));
        var args = File.ReadAllLines(_state.File("args"));
        Assert.Contains("test-model", args);
        Assert.Contains(backend == "cursor" ? "--force" : "--skip-permissions-unsafe", args);
        Assert.Contains("json", args);
        if (backend == "droid") { Assert.Contains("--reasoning-effort", args); }

        var follow = first with { JobId = "follow", Correlation = "next", Instruction = "next", ResumeSessionId = "session-1" };
        var followEvidence = await Run(agent, follow);
        Assert.Contains(new BackendEvidence.Result("next", "done"), followEvidence);
        Assert.Equal("next", File.ReadAllText(_state.File("stdin")));
        args = File.ReadAllLines(_state.File("args"));
        var resumeFlag = backend == "cursor" ? "--resume" : "--session-id";
        Assert.Equal("session-1", args[Array.IndexOf(args, resumeFlag) + 1]);
    }

    [Theory]
    [InlineData("cursor")]
    [InlineData("droid")]
    public async Task Nonzero_exit_and_wrong_session_never_complete(string backend)
    {
        if (!OperatingSystem.IsLinux()) { return; }
        var failed = FakeCli("", 1);
        IJobBackend agent = backend == "cursor" ? new CursorCliBackend(failed) : new DroidBackend(failed);
        var request = new BackendRequest("job", "corr", "task", "") { WorkingDirectory = _state.Path };
        Assert.Single(await Run(agent, request), evidence => evidence is BackendEvidence.AgentError);

        var wrong = FakeCli("""{"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"other"}""");
        agent = backend == "cursor" ? new CursorCliBackend(wrong) : new DroidBackend(wrong);
        Assert.Equal([new BackendEvidence.ProtocolError("session_expired")],
            await Run(agent, request with { ResumeSessionId = "session-1" }));
    }

    // Captured from signed-out CLIs (HOME without credentials).
    [Theory]
    [InlineData("droid", """{"type":"result","subtype":"failure","is_error":true,"duration_ms":1,"num_turns":0,"result":"Authentication failed. Please log in using /login or set a valid FACTORY_API_KEY environment variable.","session_id":"8f4ea11a-d301-4f10-93eb-d565f1841798"}""", "", 1)]
    [InlineData("droid", """{"type":"result","subtype":"failure","is_error":true,"result":"Authentication failed. Please log in using /login or set a valid FACTORY_API_KEY environment variable.","session_id":"s"}""", "", 0)]
    [InlineData("cursor", "", "Error: Authentication required. Please run 'agent login' first, or set CURSOR_API_KEY environment variable.", 1)]
    public async Task Signed_out_cli_is_login_required(string backend, string stdout, string stderr, int exit)
    {
        if (OperatingSystem.IsWindows()) { return; }
        var script = FakeCli(stdout, exit, stderr);
        IJobBackend agent = backend == "cursor" ? new CursorCliBackend(script) : new DroidBackend(script);
        var request = new BackendRequest("job", "corr", "task", "") { WorkingDirectory = _state.Path };

        var login = Assert.IsType<BackendEvidence.AgentError>(Assert.Single(await Run(agent, request)));

        Assert.Equal("agent_login_required", login.Code);
        Assert.Contains("Authentication", login.Details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cursor")]
    [InlineData("droid")]
    public async Task Successful_reply_that_mentions_login_still_completes(string backend)
    {
        if (OperatingSystem.IsWindows()) { return; }
        var script = FakeCli("""{"type":"result","subtype":"success","is_error":false,"result":"Authentication failed. Please log in using /login or set FACTORY_API_KEY; Authentication required, run agent login or set CURSOR_API_KEY.","session_id":"s"}""",
            0, "Authentication required. Please run 'agent login' first, or set CURSOR_API_KEY.");
        IJobBackend agent = backend == "cursor" ? new CursorCliBackend(script) : new DroidBackend(script);

        var evidence = await Run(agent, new BackendRequest("job", "corr", "task", "") { WorkingDirectory = _state.Path });

        Assert.Contains(evidence, e => e is BackendEvidence.Result);
    }

    [Fact]
    public void Interactive_mode_refuses_before_accepting_cursor_or_droid()
    {
        using var fixture = new JobFixture();
        var accept = new AcceptJob(fixture.Store, JobFixture.Operator, fixture.Limits, fixture.TestProfile,
            fixture.Admission, [BackendCatalog.Cursor, BackendCatalog.Droid], launchMode: "herdr");
        foreach (var backend in new[] { BackendCatalog.Cursor, BackendCatalog.Droid })
        {
            var result = accept.Execute(new SubmitJobRequest(backend, "task", null, false) { Backend = backend });
            Assert.Contains("headless-only", result.Error);
        }
        Assert.Empty(fixture.List().Execute(new ListJobsRequest()).Page!.Jobs);
        Assert.Null(fixture.List().Execute(new ListJobsRequest { Backend = BackendCatalog.Cursor }).Error);
        Assert.Null(fixture.List().Execute(new ListJobsRequest { Backend = BackendCatalog.Droid }).Error);
    }

    [Fact]
    public void Cursor_and_droid_tiers_use_their_own_settings()
    {
        var tiers = new TierMap(_state.Path, _ => []);
        tiers.Change("cursor", "high", "my-cursor-model", "none");
        tiers.Change("droid", "high", "my-droid-model", "max");
        Assert.Equal(("my-cursor-model", (string?)null), ModelSelection.Resolve("cursor", "high", "high", _ => [], tiers));
        Assert.Equal(("my-droid-model", "max"), ModelSelection.Resolve("droid", "high", "low", _ => [], tiers));
        var request = new BackendRequest("job", "corr", "task", "effort=ultra");
        Assert.Equal("max", DroidBackend.BuildArguments(request).Last());
        Assert.Throws<ArgumentException>(() => ModelSelection.Resolve("droid", null, "invalid", _ => [], tiers));
    }

    static async Task<List<BackendEvidence>> Run(IJobBackend backend, BackendRequest request)
    {
        await using var run = backend.Start(request);
        await run.DeliverAsync(CancellationToken.None);
        var evidence = new List<BackendEvidence>();
        await foreach (var item in run.ReadEvidenceAsync(CancellationToken.None)) { evidence.Add(item); }
        return evidence;
    }

    string FakeCli(string result, int exit = 0, string stderr = "")
    {
        var script = _state.File("fake-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(script, "#!/bin/sh\n" +
            "printf '%s\\n' \"$@\" > '" + _state.File("args") + "'\n" +
            "cat > '" + _state.File("stdin") + "'\n" +
            "printf '%s\\n' '" + result + "'\n" +
            "printf '%s\\n' \"" + stderr + "\" >&2\n" +
            "exit " + exit + "\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }
}
