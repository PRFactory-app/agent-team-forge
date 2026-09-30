using System.Net;
using System.Text;
using System.Text.Json;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.PRFactory;
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
        public int Claims { get; private set; }

        public HttpResponseMessage Reply(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (ManagedWire.Reply(request) is { } managed) { return managed; }
            if (path.EndsWith("/poll", StringComparison.Ordinal))
            {
                var list = string.Join(",", items.Select(i => JsonSerializer.Serialize(i, PRFactoryWorkItemJson.Default.PRFactoryWorkItem)));
                return Json("{\"workItems\":[" + list + "]" + limits + "}");
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
}
