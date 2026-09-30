using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Tests.Support;
using AgentTeamForge.Host.Features.WebConsole;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Tests.Features.WebConsole;

public sealed class WebConsoleServerTests : IAsyncLifetime
{
    readonly string _token = WebConsoleServer.NewToken();
    readonly List<IpcRequest> _forwarded = [];
    readonly HttpClient _http = new();
    WebConsoleServer _server = null!;

    Func<IpcRequest, Task<IpcResponse>> Daemon { get; set; } = r => Task.FromResult(new IpcResponse(true, Outcome: "fake", Job: new JobView("j1", "completed", "done", null, 1)));

    public async ValueTask InitializeAsync() =>
        _server = await WebConsoleServer.StartAsync(0, _token, (r, _) =>
        {
            lock (_forwarded)
            {
                _forwarded.Add(r);
            }

            return Daemon(r);
        });

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    HttpRequestMessage Api(HttpMethod method, string path, string? token = null, string? origin = null, string? json = null)
    {
        var request = new HttpRequestMessage(method, _server.Url + path.TrimStart('/'));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? _token);
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    string Origin => _server.Url.TrimEnd('/');

    HttpRequestMessage FollowUp(string json = """{"instruction":"next","idempotency_key":"k1"}""", string? origin = null, string? token = null) =>
        Api(HttpMethod.Post, "/api/jobs/j1/follow-up", token, origin ?? Origin, json);

    HttpRequestMessage Submit(WebSubmitBody body, string? token = null, string? origin = null) =>
        Api(HttpMethod.Post, "/api/jobs", token, origin ?? Origin,
            JsonSerializer.Serialize(body, WebConsoleJson.Default.WebSubmitBody));

    HttpRequestMessage Ticket(string leadId, WebJoinTicketBody body, string? token = null, string? origin = null) =>
        Api(HttpMethod.Post, $"/api/leads/{leadId}/join-ticket", token, origin ?? Origin,
            JsonSerializer.Serialize(body, WebConsoleJson.Default.WebJoinTicketBody));

    [Fact]
    public async Task Retention_settings_validate_persist_and_apply_to_live_sessions()
    {
        using var temp = new TempStateDir();
        var settingsPath = temp.File("launch-mode.json");
        LaunchModeSettings? Read() => JsonSerializer.Deserialize(File.ReadAllText(settingsPath), SetupCommandJson.Default.LaunchModeSettings);
        void Write(LaunchModeSettings value) => File.WriteAllText(settingsPath, JsonSerializer.Serialize(value, SetupCommandJson.Default.LaunchModeSettings));
        Write(new LaunchModeSettings("headless") { WebPort = 9876 });
        var settings = new InteractiveRetentionConfiguration(Read, Write);
        Daemon = r => Task.FromResult(settings.Handle(r));
        const string path = "/api/settings/retention";
        const string valid = """{"max_retained_sessions":2,"idle_close_minutes":"off"}""";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(Api(HttpMethod.Put, path, WebConsoleServer.NewToken(), Origin, valid))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Api(HttpMethod.Put, path, origin: "http://attacker.example", json: valid))).Status);
        Assert.Empty(_forwarded);
        Assert.Equal(new InteractiveRetentionSettings(), settings.Current);
        var stopped = new List<InteractiveLaunch>();
        using var retained = new RetainedSessions(stopped.Add, settings: () => settings.Current);
        for (var i = 0; i < 3; i++) { retained.Remember("s" + i, new(InteractiveAgentKind.Claude, "atf" + i, temp.Path, null, null, temp.File("b" + i))); }
        var (savedStatus, savedBody) = await Send(Api(HttpMethod.Put, path, origin: Origin, json: valid));
        Assert.True(savedBody.Ok);
        Assert.Equal(HttpStatusCode.OK, savedStatus);
        Assert.Equal(new InteractiveRetentionSettings(2, -1), new InteractiveRetentionConfiguration(Read, Write).Current);
        Assert.Equal(9876, Read()!.WebPort);
        retained.Sweep();
        Assert.Equal(2, retained.Count);
        Assert.Equal("atf0", Assert.Single(stopped).AgentName);
        foreach (var invalid in new[]
        {
            """{"max_retained_sessions":65,"idle_close_minutes":5}""",
            """{"max_retained_sessions":-1,"idle_close_minutes":5}""",
            """{"max_retained_sessions":2,"idle_close_minutes":1441}""",
            """{"max_retained_sessions":2,"idle_close_minutes":-1}""",
            """{"idle_close_minutes":"off"}""",
            """{"max_retained_sessions":2}"""
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await Send(Api(HttpMethod.Put, path, origin: Origin, json: invalid))).Status);
        }
        Assert.False(settings.Handle(new IpcRequest { Op = IpcProtocol.RetentionSettingsPut, MaxRetainedSessions = 65, IdleCloseMinutes = 5 }).Ok);
        Assert.False(settings.Handle(new IpcRequest { Op = IpcProtocol.RetentionSettingsPut, MaxRetainedSessions = 2, IdleCloseMinutes = 1441 }).Ok);
        Assert.Equal(new InteractiveRetentionSettings(2, -1), new InteractiveRetentionConfiguration(Read, Write).Current);
        Assert.Equal(HttpStatusCode.OK, (await Send(Api(HttpMethod.Get, path))).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(Api(HttpMethod.Put, path, origin: Origin,
            json: """{"max_retained_sessions":0,"idle_close_minutes":0}"""))).Status);
        retained.Sweep();
        Assert.Equal(0, retained.Count);
        Assert.Equal(3, stopped.Count);
    }

    [Fact]
    public async Task Tier_settings_require_bearer_host_and_origin_before_forwarding()
    {
        const string body = """{"backend":"codex","tier":"xhigh","model":"gpt-6-sol","effort":"xhigh"}""";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(Api(HttpMethod.Get, "/api/settings/tiers", WebConsoleServer.NewToken()))).Status);
        var badHost = Api(HttpMethod.Get, "/api/settings/tiers");
        badHost.Headers.Host = "localhost:" + _server.Port;
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await Send(badHost)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Api(HttpMethod.Put, "/api/settings/tiers", origin: "http://attacker.example", json: body))).Status);
        Assert.Empty(_forwarded);
        Assert.Equal(HttpStatusCode.OK, (await Send(Api(HttpMethod.Get, "/api/settings/tiers"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(Api(HttpMethod.Put, "/api/settings/tiers", origin: Origin, json: body))).Status);
        Assert.Equal([IpcProtocol.TierSettingsGet, IpcProtocol.TierSettingsPut], _forwarded.Select(r => r.Op));
        Assert.Equal(("codex", "xhigh", "gpt-6-sol", "xhigh"),
            (_forwarded[1].Backend, _forwarded[1].Tier, _forwarded[1].Model, _forwarded[1].Effort));
    }

    [Fact]
    public async Task New_agent_forwards_valid_options_and_directory_after_auth_checks()
    {
        var body = new WebSubmitBody("codex", "do work", "new-agent-1", Environment.CurrentDirectory,
            "high");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(Submit(body, WebConsoleServer.NewToken()))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Submit(body, origin: "http://attacker.example"))).Status);
        Assert.Empty(_forwarded);

        var (status, _) = await Send(Submit(body));
        Assert.Equal(HttpStatusCode.OK, status);
        var request = Assert.Single(_forwarded);
        Assert.Equal((IpcProtocol.JobSubmit, "codex", "high", null, Environment.CurrentDirectory),
            (request.Op, request.Backend, request.Model, request.Effort, request.Cwd));
        Assert.Equal("new-agent-1", request.IdempotencyKey);
        Assert.Matches("^codex-[0-9a-f]{8}$", request.TargetAgent);
    }

    [Theory]
    [InlineData("cursor")]
    [InlineData("droid")]
    public async Task New_agent_accepts_headless_only_backends(string backend)
    {
        var body = new WebSubmitBody(backend, "do work", backend + "-key", Environment.CurrentDirectory, "low");
        Assert.Equal(HttpStatusCode.OK, (await Send(Submit(body))).Status);
        Assert.Equal(backend, Assert.Single(_forwarded).Backend);
    }

    [Fact]
    public async Task New_agent_can_attach_to_a_lead_with_its_registered_workspace()
    {
        var lead = Guid.NewGuid().ToString("D");
        var body = new WebSubmitBody("claude", "do work", "lead-agent-1", Environment.CurrentDirectory,
            "opus", "medium", LeadSessionId: lead, Workspace: Environment.CurrentDirectory, Name: "my-agent");

        Assert.Equal(HttpStatusCode.OK, (await Send(Submit(body))).Status);
        var request = Assert.Single(_forwarded);
        Assert.Equal((lead, Environment.CurrentDirectory), (request.LeadSessionId, request.Workspace));
        Assert.Equal("my-agent", request.TargetAgent);
    }

    [Fact]
    public async Task Generated_name_is_stable_for_a_submission_retry()
    {
        var body = new WebSubmitBody("pi", "do work", "retry-key", Environment.CurrentDirectory, "medium-fast");
        await Send(Submit(body));
        await Send(Submit(body));
        Assert.Equal(2, _forwarded.Count);
        Assert.Equal(_forwarded[0].TargetAgent, _forwarded[1].TargetAgent);
        Assert.Matches("^pi-[0-9a-f]{8}$", _forwarded[0].TargetAgent);
    }

    [Theory]
    [InlineData("-bad", null)]
    [InlineData("high-fast", null)]
    [InlineData("gpt-6-sol", null)]
    [InlineData("high", "medium")]
    public async Task Unsafe_new_agent_options_have_no_effect(string? model, string? effort)
    {
        var body = new WebSubmitBody("codex", "do work", "unsafe-1", Environment.CurrentDirectory, model, effort);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Submit(body))).Status);
        Assert.Empty(_forwarded);
    }

    [Fact]
    public async Task New_agent_requires_an_existing_absolute_directory()
    {
        var relative = new WebSubmitBody("claude", "do work", "cwd-1", "relative/path", "opus");
        var absent = relative with { Cwd = Path.Combine(Path.GetTempPath(), "atf-web-absent-" + Guid.NewGuid().ToString("N")) };
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Submit(relative))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Submit(absent))).Status);
        Assert.Empty(_forwarded);
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("fake-agent")]
    [InlineData("-bad")]
    public async Task Invalid_new_agent_name_is_rejected(string name)
    {
        var body = new WebSubmitBody("claude", "do work", "named-1", Environment.CurrentDirectory, "opus", Name: name);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Submit(body))).Status);
        Assert.Empty(_forwarded);
    }

    [Fact]
    public async Task Directory_picker_requires_auth_and_lists_only_bounded_directories()
    {
        var root = Path.Combine(Path.GetTempPath(), "atf-dir-picker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (var i = 0; i < 105; i++)
            {
                Directory.CreateDirectory(Path.Combine(root, $"sub-{i:D3}"));
            }
            File.WriteAllText(Path.Combine(root, "private.txt"), "secret");
            var path = "/api/directories?path=" + Uri.EscapeDataString(root);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(Api(HttpMethod.Get, path, WebConsoleServer.NewToken()))).Status);
            var wrongHost = Api(HttpMethod.Get, path);
            wrongHost.Headers.Host = "localhost:" + _server.Port;
            Assert.Equal(HttpStatusCode.MisdirectedRequest, (await Send(wrongHost)).Status);
            Assert.Equal(HttpStatusCode.BadRequest, (await Send(Api(HttpMethod.Get, "/api/directories?path=relative"))).Status);
            Assert.Empty(_forwarded);

            using var response = await _http.SendAsync(Api(HttpMethod.Get, path), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var directories = json.RootElement.GetProperty("directories").EnumerateArray().ToArray();
            Assert.Equal(100, directories.Length);
            Assert.All(directories, entry => Assert.StartsWith("sub-", entry.GetProperty("name").GetString()));
            Assert.DoesNotContain(directories, entry => entry.GetProperty("name").GetString() == "private.txt");
            Assert.DoesNotContain("secret", json.RootElement.ToString(), StringComparison.Ordinal);
            Assert.Empty(_forwarded);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Config_and_join_ticket_use_existing_bearer_and_origin_checks()
    {
        var lead = Guid.NewGuid().ToString("D");
        var body = new WebJoinTicketBody("codex-desktop", Environment.CurrentDirectory);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Send(Api(HttpMethod.Get, "/api/config", WebConsoleServer.NewToken()))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(Ticket(lead, body, WebConsoleServer.NewToken()))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Ticket(lead, body, origin: "http://attacker.example"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Ticket(lead, body with { Name = "bad name" }))).Status);
        Assert.Empty(_forwarded);

        Assert.Equal(HttpStatusCode.OK, (await Send(Api(HttpMethod.Get, "/api/config"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(Ticket(lead, body))).Status);
        Assert.Collection(_forwarded,
            r => Assert.Equal(IpcProtocol.JobCapabilities, r.Op),
            r => Assert.Equal((IpcProtocol.ExternalTicket, lead, Environment.CurrentDirectory, "codex-desktop"),
                (r.Op, r.LeadSessionId, r.Workspace, r.MemberName)));
    }

    [Fact]
    public async Task Stop_agent_uses_the_same_bearer_and_origin_checks_as_other_mutations()
    {
        var (badTokenStatus, _) = await Send(Api(HttpMethod.Post, "/api/jobs/j1/stop-agent", WebConsoleServer.NewToken(), Origin));
        var (badOriginStatus, _) = await Send(Api(HttpMethod.Post, "/api/jobs/j1/stop-agent", origin: "http://attacker.example"));
        Assert.Equal(HttpStatusCode.Unauthorized, badTokenStatus);
        Assert.Equal(HttpStatusCode.Forbidden, badOriginStatus);
        Assert.Empty(_forwarded);

        var (acceptedStatus, _) = await Send(Api(HttpMethod.Post, "/api/jobs/j1/stop-agent", origin: Origin));
        Assert.Equal(HttpStatusCode.OK, acceptedStatus);
        Assert.Equal((IpcProtocol.JobStopAgent, "j1"), (Assert.Single(_forwarded).Op, _forwarded[0].JobId));
    }

    async Task<(HttpStatusCode Status, IpcResponse Body)> Send(HttpRequestMessage request)
    {
        using var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        var body = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), IpcJson.Default.IpcResponse)!;
        return (response.StatusCode, body);
    }

    [Fact]
    public async Task Activity_endpoint_forwards_cursor_and_limit_under_existing_auth_checks()
    {
        Daemon = _ => Task.FromResult(new IpcResponse(true, Outcome: "activity", Activity: new JobActivityPage([new ActivityEntry("2026-01-01T00:00:00Z", "assistant_text", "hi")], 42)));
        var (status, body) = await Send(Api(HttpMethod.Get, "/api/jobs/j1/activity?after_cursor=12&limit=3"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("hi", Assert.Single(body.Activity!.Entries).Text);
        Assert.Equal(42, body.Activity.NextCursor);
        var request = Assert.Single(_forwarded);
        Assert.Equal(IpcProtocol.JobActivity, request.Op);
        Assert.Equal(12, request.AfterCursor);
        Assert.Equal(3, request.Limit);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(Api(HttpMethod.Get, "/api/jobs/j1/activity", token: WebConsoleServer.NewToken()))).Status);
    }

    [Fact]
    public async Task Page_and_api_carry_the_security_headers()
    {
        using var page = await _http.GetAsync(_server.Url, TestContext.Current.CancellationToken);
        using var api = await _http.SendAsync(Api(HttpMethod.Get, "/api/jobs"), TestContext.Current.CancellationToken);
        foreach (var response in new[] { page, api })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
            Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
            Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
            Assert.False(response.Headers.Contains("Set-Cookie"));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }
    }

    [Theory]
    [InlineData("localhost:{port}")]
    [InlineData("attacker.example:{port}")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:{other}")]
    public async Task Wrong_host_is_refused_before_any_daemon_call(string host)
    {
        var request = FollowUp();
        request.Headers.Host = host.Replace("{port}", $"{_server.Port}", StringComparison.Ordinal)
            .Replace("{other}", $"{_server.Port + 1}", StringComparison.Ordinal);

        var (status, body) = await Send(request);

        Assert.Equal((HttpStatusCode.MisdirectedRequest, WebConsoleServer.BadHost), (status, body.Error));
        Assert.Empty(_forwarded);
    }

    [Fact]
    public async Task Missing_or_wrong_bearer_has_no_effect()
    {
        var noAuth = FollowUp();
        noAuth.Headers.Authorization = null;
        var results = new[]
        {
            await Send(noAuth),
            await Send(FollowUp(token: WebConsoleServer.NewToken())),
            await Send(Api(HttpMethod.Get, "/api/jobs", token: _token + "x")),
        };

        Assert.All(results, r => Assert.Equal((HttpStatusCode.Unauthorized, WebConsoleServer.Unauthorized), (r.Status, r.Body.Error)));
        Assert.Empty(_forwarded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://localhost")]
    [InlineData("null")]
    [InlineData("https://evil.example")]
    public async Task Post_without_the_exact_origin_has_no_effect(string? origin)
    {
        var request = FollowUp(origin: "placeholder");
        request.Headers.Remove("Origin");
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        var (status, body) = await Send(request);

        Assert.Equal((HttpStatusCode.Forbidden, WebConsoleServer.ForbiddenOrigin), (status, body.Error));
        Assert.Empty(_forwarded);
    }

    [Theory]
    [InlineData("""{"instruction":"","idempotency_key":"k1"}""")]
    [InlineData("""{"instruction":"x"}""")]
    [InlineData("""not json""")]
    public async Task Invalid_follow_up_body_has_no_effect(string json)
    {
        var (status, _) = await Send(FollowUp(json));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(_forwarded);
    }

    [Fact]
    public async Task Oversized_body_has_no_effect()
    {
        var big = new string('a', WebConsoleServer.MaxBodyBytes + 1);
        var (status, _) = await Send(FollowUp($$"""{"instruction":"{{big}}","idempotency_key":"k1"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(_forwarded);
    }

    [Fact]
    public async Task Max_length_non_ascii_instruction_is_forwarded()
    {
        var text = new string('\u00e5', WebConsoleServer.MaxInstructionChars);
        var json = JsonSerializer.Serialize(new WebFollowUpBody(text, "k1"), WebConsoleJson.Default.WebFollowUpBody);
        var (status, body) = await Send(FollowUp(json));

        Assert.Equal((HttpStatusCode.OK, true), (status, body.Ok));
        Assert.Equal(text, Assert.Single(_forwarded).Instruction);
    }

    [Fact]
    public async Task Submit_and_composer_use_the_daemon_instruction_limit()
    {
        Assert.Equal(WebConsoleServer.MaxInstructionChars, new AgentTeamForge.Business.SpikeLimits().MaxInstructionChars);
        var instruction = new string('z', 20_000);
        Assert.Equal(HttpStatusCode.OK, (await Send(Submit(new WebSubmitBody("codex", instruction, "long-submit", Environment.CurrentDirectory, "high")))).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(FollowUp(JsonSerializer.Serialize(new WebFollowUpBody(instruction, "long-follow"), WebConsoleJson.Default.WebFollowUpBody)))).Status);
        Assert.Equal(2, _forwarded.Count);
        Assert.All(_forwarded, request => Assert.Equal(instruction, request.Instruction));

        var tooLong = new string('z', WebConsoleServer.MaxInstructionChars + 1);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Submit(new WebSubmitBody("codex", tooLong, "too-long", Environment.CurrentDirectory, "high")))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(FollowUp(JsonSerializer.Serialize(new WebFollowUpBody(tooLong, "too-long-follow"), WebConsoleJson.Default.WebFollowUpBody)))).Status);
        Assert.Equal(2, _forwarded.Count);
    }

    [Fact]
    public async Task List_get_and_follow_up_forward_exactly_one_matching_ipc_call_each()
    {
        await Send(Api(HttpMethod.Get, "/api/jobs"));
        await Send(Api(HttpMethod.Get, "/api/jobs/j1"));
        var (status, body) = await Send(FollowUp("""{"instruction":"next turn","idempotency_key":"key-7"}"""));

        Assert.Equal((HttpStatusCode.OK, true, "fake"), (status, body.Ok, body.Outcome));
        Assert.Collection(_forwarded,
            r => Assert.Equal(IpcProtocol.JobList, r.Op),
            r => Assert.Equal((IpcProtocol.JobGet, "j1"), (r.Op, r.JobId)),
            r => Assert.Equal((IpcProtocol.JobFollowUp, "j1", "next turn", "key-7"), (r.Op, r.JobId, r.Instruction, r.IdempotencyKey)));
        Assert.All(_forwarded, r => Assert.Null(r.Credential));
        Assert.True(_forwarded[0].IncludeUsage);
        Assert.All(_forwarded.Skip(1), r => Assert.False(r.IncludeUsage));
    }

    [Fact]
    public async Task Web_usage_serializes_unknown_session_totals_as_null()
    {
        Daemon = _ => Task.FromResult(new IpcResponse(true,
            Page: new JobListPage([new JobSummary("j1", "running", null, 1, "now", "now")], 50, false, null),
            LeadTokens: new Dictionary<string, AgentTeamForge.Business.Features.Usage.TokenUsage?> { ["lead"] = null }));
        using var response = await _http.SendAsync(Api(HttpMethod.Get, "/api/jobs"), TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("page").GetProperty("jobs")[0].GetProperty("session_tokens").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("lead_tokens").GetProperty("lead").ValueKind);
    }

    [Fact]
    public async Task Unknown_outcome_is_reported_as_is_without_retry()
    {
        Daemon = _ => Task.FromResult(new IpcResponse(false, IpcProtocol.OutcomeUnknown));

        var (status, body) = await Send(FollowUp());

        Assert.Equal((HttpStatusCode.OK, false, IpcProtocol.OutcomeUnknown), (status, body.Ok, body.Error));
        Assert.Single(_forwarded);
    }

    [Fact]
    public async Task Calls_beyond_the_concurrency_bound_are_refused_not_queued()
    {
        var gate = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        Daemon = _ => gate.Task;
        var inFlight = Enumerable.Range(0, WebConsoleServer.MaxConcurrentCalls).Select(_ => Send(Api(HttpMethod.Get, "/api/jobs"))).ToList();
        await Support.Bounded.Until(() => { lock (_forwarded) { return _forwarded.Count == WebConsoleServer.MaxConcurrentCalls; } }, "calls in flight");

        var (status, body) = await Send(FollowUp());

        Assert.Equal((HttpStatusCode.ServiceUnavailable, WebConsoleServer.Busy), (status, body.Error));
        Assert.DoesNotContain(_forwarded, r => r.Op == IpcProtocol.JobFollowUp);
        gate.SetResult(new IpcResponse(true));
        await Task.WhenAll(inFlight);
    }

    [Fact]
    public async Task Stop_and_output_routes_forward_to_job_ipc()
    {
        var (stopStatus, _) = await Send(Api(HttpMethod.Post, "/api/jobs/j1/stop", origin: Origin));
        var (outputStatus, _) = await Send(Api(HttpMethod.Get, "/api/jobs/j1/output?offset=5"));
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (stopStatus, outputStatus));
        Assert.Collection(_forwarded,
            r => Assert.Equal((IpcProtocol.JobStop, "j1"), (r.Op, r.JobId)),
            r => Assert.Equal((IpcProtocol.JobOutput, "j1", 5L), (r.Op, r.JobId, r.Offset)));
    }

    [Fact]
    public async Task History_filter_and_cursor_are_forwarded()
    {
        var (status, _) = await Send(Api(HttpMethod.Get,
            "/api/jobs?status=needs_reconciliation&cursor=job_012345"));

        Assert.Equal(HttpStatusCode.OK, status);
        var call = Assert.Single(_forwarded);
        Assert.Equal((IpcProtocol.JobList, "needs_reconciliation", "job_012345"),
            (call.Op, call.Status, call.Cursor));
        Assert.True(call.IncludeConnector);
        Assert.Equal(ListJobs.MaxPageSize, call.Limit);
    }

    [Fact]
    public async Task Interrupt_follow_up_forwards_the_flag_once()
    {
        var (status, _) = await Send(FollowUp("""{"instruction":"redirect","idempotency_key":"interrupt-1","interrupt":true}"""));

        Assert.Equal(HttpStatusCode.OK, status);
        var call = Assert.Single(_forwarded);
        Assert.Equal((IpcProtocol.JobFollowUp, "j1", true), (call.Op, call.JobId, call.Interrupt));
    }

    [Fact]
    public async Task Output_read_is_bounded_by_the_route()
    {
        await Send(Api(HttpMethod.Get, "/api/jobs/j1/output?offset=11&max_bytes=999999"));

        var call = Assert.Single(_forwarded);
        Assert.Equal((IpcProtocol.JobOutput, 11L, 65536), (call.Op, call.Offset, call.MaxBytes));
    }
}
