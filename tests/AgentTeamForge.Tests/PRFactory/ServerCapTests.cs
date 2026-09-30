using System.Net;
using System.Text;
using System.Text.Json;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class ServerCapTests
{
    const string Url = "https://example.test";

    static PRFactoryWorkItem NewItem(Guid repo) => new()
    {
        Id = Guid.NewGuid(),
        RepositoryId = repo,
        ReadOnly = true,
        AgentType = PRFactoryAgentType.Codex,
        Prompt = "Do work"
    };

    sealed class Server(PRFactoryWorkItem[] items, string limits, HttpStatusCode claimStatus = HttpStatusCode.OK)
    {
        public string Limits { get; set; } = limits;
        public int Polls { get; private set; }
        public int Claims { get; private set; }

        public HttpResponseMessage Reply(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (ManagedWire.Reply(request) is { } managed) { return managed; }
            if (path.EndsWith("/poll", StringComparison.Ordinal))
            {
                Polls++;
                var list = string.Join(",", items.Select(i => JsonSerializer.Serialize(i, PRFactoryWorkItemJson.Default.PRFactoryWorkItem)));
                return Json("{\"workItems\":[" + list + "]" + Limits + "}");
            }
            if (path.Contains("/claim/", StringComparison.Ordinal))
            {
                Claims++;
                if (claimStatus != HttpStatusCode.OK) { return new HttpResponseMessage(claimStatus); }
                var id = Guid.Parse(path[(path.LastIndexOf('/') + 1)..]);
                return Json("{\"workItem\":" + JsonSerializer.Serialize(items.Single(i => i.Id == id), PRFactoryWorkItemJson.Default.PRFactoryWorkItem) + "}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }

    static async Task<(Server Server, int Teams, PRFactoryServerLimit? Limit)> Tick(
        int count, string limits, HttpStatusCode claimStatus = HttpStatusCode.OK)
    {
        using var dir = new TempStateDir();
        var db = JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2));
        var repo = Guid.NewGuid();
        var items = Enumerable.Range(0, count).Select(_ => NewItem(repo)).ToArray();
        var server = new Server(items, limits, claimStatus);
        var teams = new PRFactoryTeamStore(db);
        var client = new PRFactoryClient(PRFactoryClient.CreateHttpClient(Url, "token", new Handler(server.Reply)));
        PRFactoryServerLimit? seen = null;
        var adapter = new PRFactoryWorkItems(Url, [new RepositoryMapping(repo, dir.Path)], teams, client,
            _ => JobResult.Ok(new JobView("lead-job", JobStatus.Running, null, null, 0), "accepted"),
            _ => null, () => { }, onLimit: l => seen = l);
        await adapter.TickAsync(null, CancellationToken.None);
        return (server, items.Count(i => teams.Get(Url, i.Id) is not null), seen);
    }

    [Fact]
    public async Task Poll_at_cap_claims_nothing()
    {
        var (server, _, limit) = await Tick(2, ",\"maxConcurrentWorkItems\":3,\"activeWorkItems\":3");
        Assert.Equal(0, server.Claims);
        Assert.Equal(new(3, 3), (limit!.Max, limit.Active));
    }

    [Fact]
    public async Task Poll_without_limit_fields_claims_every_item()
    {
        var (server, teams, limit) = await Tick(2, "");
        Assert.Null(limit);
        Assert.Equal(2, server.Claims);
        Assert.Equal(2, teams);
    }

    [Fact]
    public async Task Poll_below_cap_claims_several_items_in_one_tick()
    {
        var (server, teams, _) = await Tick(2, ",\"maxConcurrentWorkItems\":10,\"activeWorkItems\":1");
        Assert.Equal(2, server.Claims);
        Assert.Equal(2, teams);
    }

    [Fact]
    public async Task Claim_conflict_stops_further_claims_in_the_tick()
    {
        var (server, teams, _) = await Tick(3, "", HttpStatusCode.Conflict);
        Assert.Equal(1, server.Claims);
        Assert.Equal(0, teams);
    }

    [Fact]
    public async Task Repo_less_only_connection_resumes_polling_after_the_cap_lifts()
    {
        using var dir = new TempStateDir();
        var db = JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2));
        var item = NewItem(Guid.NewGuid());
        item.RepositoryId = null;
        var server = new Server([item], ",\"maxConcurrentWorkItems\":1,\"activeWorkItems\":1");
        var teams = new PRFactoryTeamStore(db);
        // One client across ticks, exactly as PRFactoryHeartbeat keeps it.
        var client = new PRFactoryClient(PRFactoryClient.CreateHttpClient(Url, "token", new Handler(server.Reply)));
        var adapter = new PRFactoryWorkItems(Url, [], teams, client,
            _ => JobResult.Ok(new JobView("lead-job", JobStatus.Running, null, null, 0), "accepted"),
            _ => null, () => { }, allowRepoLess: true);

        await adapter.TickAsync(null, CancellationToken.None);
        Assert.Equal(1, server.Polls);
        Assert.Equal(0, server.Claims);

        server.Limits = ",\"maxConcurrentWorkItems\":1,\"activeWorkItems\":0";
        await adapter.TickAsync(null, CancellationToken.None);
        Assert.Equal(2, server.Polls);
        Assert.Equal(1, server.Claims);
    }

    [Fact]
    public async Task Accepted_teams_still_advance_while_the_server_is_at_cap()
    {
        using var dir = new TempStateDir();
        var db = JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2));
        var repo = Guid.NewGuid();
        var item = NewItem(repo);
        var server = new Server([item], "");
        var teams = new PRFactoryTeamStore(db);
        var client = new PRFactoryClient(PRFactoryClient.CreateHttpClient(Url, "token", new Handler(server.Reply)));
        var status = JobStatus.Running;
        var adapter = new PRFactoryWorkItems(Url, [new RepositoryMapping(repo, dir.Path)], teams, client,
            _ => JobResult.Ok(new JobView("lead-job", JobStatus.Running, null, null, 0), "accepted"),
            id => new JobRecord(id, "prfactory", "connector", "lead", "key", "prompt", "", status,
                null, null, 0, "codex", null, null, null) with
            { Cwd = dir.Path }, () => { });
        await adapter.TickAsync(null, CancellationToken.None);
        Assert.Single(teams.Pending(Url));

        status = JobStatus.Completed;
        server.Limits = ",\"maxConcurrentWorkItems\":1,\"activeWorkItems\":1";
        await adapter.TickAsync(null, CancellationToken.None);
        Assert.Equal(1, server.Claims);
        Assert.Empty(teams.Pending(Url));
    }

    [Fact]
    public void Disconnect_removes_the_limit_file_and_a_stale_value_is_not_resurrected()
    {
        using var dir = new TempStateDir();
        var state = StateDirectory.Open(dir.Path);
        var options = new Dictionary<string, string> { ["url"] = Url, ["token-scope"] = "tenant-wide" };
        Assert.Equal(0, PRFactoryConnection.Run(state, "connect", options, [], new StringReader("fake-token")));
        PRFactoryConnection.PublishLimit(state, new PRFactoryServerLimit(3, 3, DateTimeOffset.UtcNow));
        var path = Path.Combine(dir.Path, "prfactory-limit.json");
        Assert.True(File.Exists(path));

        Assert.Equal(0, PRFactoryConnection.Run(state, "disconnect", options, [], new StringReader("")));
        Assert.False(File.Exists(path));

        // After reconnecting, a fresh client has no limit and nothing rewrites the old value.
        Assert.Equal(0, PRFactoryConnection.Run(state, "connect", options, [], new StringReader("fake-token")));
        Assert.False(File.Exists(path));
        PRFactoryConnection.PublishLimit(state, new PRFactoryClient(new HttpClient()).Limit);
        Assert.False(File.Exists(path));
    }
}
