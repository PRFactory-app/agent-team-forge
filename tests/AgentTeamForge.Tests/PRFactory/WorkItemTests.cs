using System.Net;
using System.Text;
using System.Text.Json;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class WorkItemTests
{
    [Fact]
    public async Task Claim_team_jobs_artefacts_complete_and_restart_do_not_spawn_twice()
    {
        using var dir = new TempStateDir();
        var db = JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2));
        var teams = new PRFactoryTeamStore(db);
        var server = new FakeServer(new PRFactoryWorkItem
        {
            Id = Guid.NewGuid(), RepositoryId = Guid.NewGuid(), AgentType = PRFactoryAgentType.Codex,
            Prompt = "Do work", ExpectedOutput = "result.md", TicketArtefactFolder = "output",
            TeamPlan = new PRFactoryTeamPlan
            {
                RecipeName = "review", RecipeVersion = 1, MaxConcurrentChildren = 1, FreeRoomCeiling = 0,
                Members =
                [
                    new PRFactoryTeamMember { Name = "lead", IsLead = true },
                    new PRFactoryTeamMember { Name = "reviewer", Role = "Review", Backend = PRFactoryAgentType.ClaudeCode, Model = "opus", Effort = PRFactoryEffort.High, Order = 1 }
                ]
            }
        });
        Directory.CreateDirectory(dir.File("output"));
        File.WriteAllText(dir.File("output/result.md"), "Artefact text");
        var jobs = new Dictionary<string, JobRecord>();
        var spawns = 0;
        JobResult Submit(SubmitJobRequest request)
        {
            spawns++;
            Assert.Equal(dir.Path, request.Cwd);
            var id = "job_" + spawns;
            jobs[id] = NewJob(id, request.Backend!);
            return JobResult.Ok(new JobView(id, JobStatus.Queued, null, null, 0), "accepted");
        }
        PRFactoryWorkItems Adapter() => new("https://example.test", [new RepositoryMapping(server.Item.RepositoryId, dir.Path)],
            teams, new PRFactoryClient(PRFactoryClient.CreateHttpClient("https://example.test", "token", new FakeHandler(server.Reply))),
            Submit, id => jobs.GetValueOrDefault(id), () => { });
        var first = Adapter();
        await first.TickAsync(null, CancellationToken.None);
        Assert.Equal(2, spawns);
        Assert.Equal(0, server.Uploads);
        foreach (var id in jobs.Keys.ToArray()) jobs[id] = jobs[id] with { Status = JobStatus.Completed, ResultText = "Done" };
        var restarted = Adapter();
        await restarted.TickAsync(null, CancellationToken.None);
        await restarted.TickAsync(null, CancellationToken.None); // Poll offers the same item again.
        Assert.Equal(2, spawns);
        Assert.Equal(1, server.Claims);
        Assert.Equal(1, server.Uploads);
        Assert.Equal("Artefact text", server.UploadContent);
        Assert.Equal(1, server.Completions);
        Assert.Equal("completed", teams.Get("https://example.test", server.Item.Id)!.State);
    }

    [Fact]
    public async Task Multi_repository_claim_is_refused_before_any_job_and_upload_precedes_failure()
    {
        using var dir = new TempStateDir();
        var db = JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2));
        var teams = new PRFactoryTeamStore(db);
        var server = new FakeServer(new PRFactoryWorkItem
        {
            Id = Guid.NewGuid(), RepositoryId = Guid.NewGuid(), Prompt = "Do work",
            ContextJson = JsonSerializer.Serialize(new { repositories = new { secondary = new[] { new { id = Guid.NewGuid() } } } })
        });
        var spawns = 0;
        var adapter = new PRFactoryWorkItems("https://example.test", [new RepositoryMapping(server.Item.RepositoryId, dir.Path)],
            teams, new PRFactoryClient(PRFactoryClient.CreateHttpClient("https://example.test", "token", new FakeHandler(server.Reply))),
            _ => { spawns++; throw new Exception("should not submit"); }, _ => null, () => { });
        await adapter.TickAsync(null, CancellationToken.None);
        Assert.Equal(0, spawns);
        Assert.Equal(["poll", "claim", "artefacts", "fail"], server.Calls);
        Assert.Equal("refused", teams.Get("https://example.test", server.Item.Id)!.State);
    }

    static JobRecord NewJob(string id, string backend) =>
        new(id, "prfactory", "connector", "connector-lead", id, "prompt", "", JobStatus.Queued,
            null, null, 0, backend, null, null, null);

    sealed class FakeServer(PRFactoryWorkItem item)
    {
        public PRFactoryWorkItem Item { get; } = item;
        public int Claims { get; private set; }
        public int Uploads { get; private set; }
        public int Completions { get; private set; }
        public string? UploadContent { get; private set; }
        public List<string> Calls { get; } = [];

        public HttpResponseMessage Reply(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/poll", StringComparison.Ordinal))
            {
                Calls.Add("poll");
                return Json(new { workItems = new[] { Item } });
            }
            if (path.Contains("/claim/", StringComparison.Ordinal))
            {
                Claims++;
                Calls.Add("claim");
                return Json(new { workItem = Item });
            }
            if (path.Contains("/artefacts/", StringComparison.Ordinal))
            {
                Uploads++;
                Calls.Add("artefacts");
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var json = JsonDocument.Parse(body);
                var artefacts = json.RootElement.GetProperty("artefacts");
                UploadContent = artefacts.GetArrayLength() > 0 ? artefacts[0].GetProperty("content").GetString() : null;
                return Json(new { accepted = true });
            }
            if (path.Contains("/complete/", StringComparison.Ordinal))
            {
                Completions++;
                Calls.Add("complete");
                return Json(new { accepted = true });
            }
            if (path.Contains("/fail/", StringComparison.Ordinal))
            {
                Calls.Add("fail");
                return Json(new { acknowledged = true });
            }
            throw new InvalidOperationException(path);
        }

        static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            }), Encoding.UTF8, "application/json")
        };
    }

    sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }
}
