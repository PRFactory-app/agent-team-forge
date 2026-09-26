using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentTeamForge.Business.Features.Jobs;
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
    public async Task New_agent_forwards_valid_options_and_directory_after_auth_checks()
    {
        var body = new WebSubmitBody("codex", "do work", "new-agent-1", Environment.CurrentDirectory,
            "gpt-5.3-codex", "high");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(Submit(body, WebConsoleServer.NewToken()))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Submit(body, origin: "http://attacker.example"))).Status);
        Assert.Empty(_forwarded);

        var (status, _) = await Send(Submit(body));
        Assert.Equal(HttpStatusCode.OK, status);
        var request = Assert.Single(_forwarded);
        Assert.Equal((IpcProtocol.JobSubmit, "codex", "gpt-5.3-codex", "high", Environment.CurrentDirectory),
            (request.Op, request.Backend, request.Model, request.Effort, request.Cwd));
        Assert.Equal("new-agent-1", request.IdempotencyKey);
    }

    [Fact]
    public async Task New_agent_can_attach_to_a_lead_with_its_registered_workspace()
    {
        var lead = Guid.NewGuid().ToString("D");
        var body = new WebSubmitBody("claude", "do work", "lead-agent-1", Environment.CurrentDirectory,
            LeadSessionId: lead, Workspace: Environment.CurrentDirectory);

        Assert.Equal(HttpStatusCode.OK, (await Send(Submit(body))).Status);
        var request = Assert.Single(_forwarded);
        Assert.Equal((lead, Environment.CurrentDirectory), (request.LeadSessionId, request.Workspace));
    }

    [Theory]
    [InlineData("-bad", null)]
    [InlineData("ok;rm", null)]
    [InlineData("ok'quoted", null)]
    [InlineData("ok$HOME", null)]
    [InlineData(null, "high&echo")]
    public async Task Unsafe_new_agent_options_have_no_effect(string? model, string? effort)
    {
        var body = new WebSubmitBody("codex", "do work", "unsafe-1", Environment.CurrentDirectory, model, effort);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Submit(body))).Status);
        Assert.Empty(_forwarded);
    }

    [Fact]
    public async Task New_agent_requires_an_existing_absolute_directory()
    {
        var relative = new WebSubmitBody("claude", "do work", "cwd-1", "relative/path");
        var absent = relative with { Cwd = Path.Combine(Path.GetTempPath(), "atf-web-absent-" + Guid.NewGuid().ToString("N")) };
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Submit(relative))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Submit(absent))).Status);
        Assert.Empty(_forwarded);
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
