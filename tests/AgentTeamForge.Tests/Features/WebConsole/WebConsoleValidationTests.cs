using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Features.WebConsole;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.WebConsole;

/// <summary>The console over a real <see cref="JobsEndpoint"/>: follow-up queueing and field-level rejections.</summary>
public sealed class WebConsoleValidationTests : IAsyncLifetime
{
    const string Catalog = """
        {"models":[
          {"slug":"gpt-6-luna","supported_in_api":true,"visibility":"list","supported_reasoning_levels":[{"effort":"low"},{"effort":"medium"},{"effort":"high"},{"effort":"xhigh"},{"effort":"max"}]},
          {"slug":"gpt-6.1-sol","supported_in_api":true,"visibility":"list","supported_reasoning_levels":[{"effort":"low"},{"effort":"medium"},{"effort":"high"},{"effort":"xhigh"},{"effort":"max"},{"effort":"ultra"}]},
          {"slug":"gpt-6-astra","supported_in_api":true,"visibility":"list","supported_reasoning_levels":[{"effort":"low"},{"effort":"ultra"}]}
        ]}
        """;

    readonly JobFixture _jobs = new();
    readonly TempStateDir _state = new();
    readonly string _token = WebConsoleServer.NewToken();
    readonly HttpClient _http = new();
    WebConsoleServer _server = null!;

    public async ValueTask InitializeAsync()
    {
        var accept = _jobs.Accept();
        var tiers = new TierMap(_state.Path, backend => backend == "codex" ? BackendModelDiscovery.ParseCodex(Catalog) : []);
        var endpoint = new JobsEndpoint(accept, _jobs.Get(), new FollowUpJob(_jobs.Store, JobFixture.Operator, accept), _jobs.List(),
            new StopJob(_jobs.Store, JobFixture.Operator, _ => { }), new DurabilityCheckpoints(null), () => { }, tierMap: tiers);
        _server = await WebConsoleServer.StartAsync(0, _token, (request, _) => Task.FromResult(endpoint.Handle(request)));
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        _state.Dispose();
        _jobs.Dispose();
    }

    async Task<(HttpStatusCode Status, JsonObject Body)> Send(HttpMethod method, string path, string? json = null)
    {
        using var request = new HttpRequestMessage(method, _server.Url + path.TrimStart('/'));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (json is not null)
        {
            request.Headers.Add("Origin", _server.Url.TrimEnd('/'));
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        using var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, JsonNode.Parse(text)!.AsObject());
    }

    [Fact]
    public async Task Follow_up_to_a_running_job_queues_behind_it_like_the_cli_and_mcp()
    {
        var parent = _jobs.Submit("parent", "first");
        var claim = _jobs.Store.BeginNextAttempt()!;
        _jobs.Store.RecordSession(new RunRef(parent.JobId, claim.RunId, claim.Generation, claim.Correlation), "native");

        var (status, body) = await Send(HttpMethod.Post, $"/api/jobs/{parent.JobId}/follow-up",
            """{"instruction":"next","idempotency_key":"web-busy"}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("accepted", (string?)body["outcome"]);
        var child = _jobs.Store.GetJob((string)body["job"]!["job_id"]!)!;
        Assert.Equal((JobStatus.Queued, parent.JobId), (child.Status, child.ParentJobId));
        Assert.Equal(JobStatus.Running, _jobs.Store.GetJob(parent.JobId)!.Status);
    }

    [Theory]
    [InlineData("cheapest", "gpt-6-luna", "ultra", "effort", "Supported: low, medium, high, xhigh, max")]
    [InlineData("cheapest", "gpt-6-luna", "bogus-eff", "effort", "Unsupported effort 'bogus-eff'")]
    [InlineData("cheapest", "gpt-6-missing", "low", "model", "not available for codex")]
    [InlineData("medium-fast", "gpt-6-luna", "low", "tier", "Unknown tier 'medium-fast' for codex")]
    public async Task Rejected_tier_settings_return_400_naming_the_field(string tier, string model, string effort, string field, string detail)
    {
        var (status, body) = await Send(HttpMethod.Put, "/api/settings/tiers",
            JsonSerializer.Serialize(new WebTierBody("codex", tier, model, effort), WebConsoleJson.Default.WebTierBody));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal((false, JobErrors.InvalidRequest, field), ((bool)body["ok"]!, (string?)body["error"], (string?)body["field"]));
        Assert.Contains(detail, (string)body["error_detail"]!, StringComparison.Ordinal);
        Assert.False(File.Exists(_state.File("tier-map.json")));
    }

    [Fact]
    public async Task Tier_rows_offer_the_efforts_the_catalog_reports_for_their_model()
    {
        var (saved, _) = await Send(HttpMethod.Put, "/api/settings/tiers",
            """{"backend":"codex","tier":"high","model":"gpt-6.1-sol","effort":"ultra"}""");
        Assert.Equal(HttpStatusCode.OK, saved);

        var (status, body) = await Send(HttpMethod.Get, "/api/settings/tiers");
        Assert.Equal(HttpStatusCode.OK, status);
        string[] Efforts(string backend, string tier) => [.. body["tiers"]!.AsArray()
            .Single(row => (string?)row!["backend"] == backend && (string?)row["tier"] == tier)!["efforts"]!.AsArray()
            .Select(effort => (string)effort!)];
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], Efforts("codex", "cheapest"));
        Assert.Equal(["low", "medium", "high", "xhigh", "max", "ultra"], Efforts("codex", "high"));
        Assert.Equal(ModelSelection.Efforts("pi"), Efforts("pi", "high"));
    }

    [Fact]
    public async Task Tier_settings_list_the_efforts_of_every_catalog_model_so_a_model_change_can_offer_them()
    {
        var (status, body) = await Send(HttpMethod.Get, "/api/settings/tiers");

        Assert.Equal(HttpStatusCode.OK, status);
        string[] Efforts(string backend, string model) => [.. body["model_efforts"]![backend]![model]!.AsArray().Select(effort => (string)effort!)];
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], Efforts("codex", "gpt-6-luna"));
        Assert.Equal(["low", "medium", "high", "xhigh", "max", "ultra"], Efforts("codex", "gpt-6.1-sol"));
        Assert.Equal(["low", "ultra"], Efforts("codex", "gpt-6-astra"));
    }

    [Theory]
    [InlineData("""{"max_retained_sessions":65,"idle_close_minutes":5}""", "max_retained_sessions")]
    [InlineData("""{"max_retained_sessions":2,"idle_close_minutes":1441}""", "idle_close_minutes")]
    public async Task Rejected_retention_settings_name_the_field(string json, string field)
    {
        var (status, body) = await Send(HttpMethod.Put, "/api/settings/retention", json);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal((WebConsoleServer.BadRequest, field), ((string?)body["error"], (string?)body["field"]));
        Assert.False(string.IsNullOrEmpty((string?)body["error_detail"]));
    }

    [Theory]
    [InlineData("claude", "opus", "bogus-eff", "effort", "Supported: low, medium, high, xhigh, max")]
    [InlineData("claude", "nope", null, "model", "Supported: opus, sonnet, haiku, fable, fast, balanced, powerful")]
    [InlineData("codex", "cheapest", "ultra", "effort", "tier sets the effort")]
    public async Task Rejected_new_agent_options_name_the_field(string backend, string model, string? effort, string field, string detail)
    {
        var json = JsonSerializer.Serialize(new WebSubmitBody(backend, "do work", "new-1", Environment.CurrentDirectory, model, effort),
            WebConsoleJson.Default.WebSubmitBody);

        var (status, body) = await Send(HttpMethod.Post, "/api/jobs", json);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(field, (string?)body["field"]);
        Assert.Contains(detail, (string)body["error_detail"]!, StringComparison.Ordinal);
        Assert.Empty(_jobs.List().Execute(new ListJobsRequest()).Page!.Jobs);
    }
}
