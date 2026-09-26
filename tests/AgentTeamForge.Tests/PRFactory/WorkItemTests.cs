using System.Net;
using System.Text;
using System.Text.Json;
using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Features.Sessions;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class WorkItemTests
{
    [Fact]
    public async Task Lost_local_submit_reply_reuses_one_accepted_job_after_restart()
    {
        using var dir = new TempStateDir();
        var db = JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2));
        var teams = new PRFactoryTeamStore(db);
        var jobs = new JobStore(db, DurabilityCheckpoints.None);
        var accept = new AcceptJob(jobs, new BoundPrincipal("prfactory", "connector", "connector-lead"),
            new SpikeLimits(), false, new AdmissionGate(), ["codex"]);
        var item = new PRFactoryWorkItem
        {
            Id = Guid.NewGuid(),
            RepositoryId = Guid.NewGuid(),
            AgentType = PRFactoryAgentType.Codex,
            Prompt = "Do work"
        };
        var server = new FakeServer(item);
        var loseReply = true;
        JobResult Submit(SubmitJobRequest request)
        {
            var outcome = accept.Execute(request);
            if (loseReply)
            {
                loseReply = false;
                throw new IOException("local acceptance reply lost");
            }
            return outcome;
        }
        PRFactoryWorkItems Adapter() => new("https://example.test", [new RepositoryMapping(item.RepositoryId, dir.Path)],
            teams, new PRFactoryClient(PRFactoryClient.CreateHttpClient("https://example.test", "token", new FakeHandler(server.Reply))),
            Submit, jobs.GetJob, () => { });
        await Assert.ThrowsAsync<IOException>(() => Adapter().TickAsync(null, CancellationToken.None));
        Assert.NotNull(teams.Get("https://example.test", item.Id));
        Assert.Single(jobs.ListJobs("prfactory", "connector", 10));
        await Adapter().TickAsync(null, CancellationToken.None);
        Assert.Single(jobs.ListJobs("prfactory", "connector", 10));
        Assert.Equal(1, server.Claims);
    }

    [Fact]
    public async Task Claim_team_jobs_artefacts_complete_and_restart_do_not_spawn_twice()
    {
        using var dir = new TempStateDir();
        var db = JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2));
        var teams = new PRFactoryTeamStore(db);
        var sessions = new LeadSessionStore(db);
        var leadSession = sessions.Start(dir.Path, "prfactory:https://example.test").SessionId;
        var server = new FakeServer(new PRFactoryWorkItem
        {
            Id = Guid.NewGuid(),
            RepositoryId = Guid.NewGuid(),
            AgentType = PRFactoryAgentType.Codex,
            Prompt = "Do work",
            ExpectedOutput = "result.md",
            TicketArtefactFolder = "output",
            TeamPlan = new PRFactoryTeamPlan
            {
                RecipeName = "review",
                RecipeVersion = 1,
                MaxConcurrentChildren = 1,
                FreeRoomCeiling = 0,
                Members =
                [
                    new PRFactoryTeamMember { Name = "lead", IsLead = true },
                    new PRFactoryTeamMember { Name = "reviewer", Role = "Review", Backend = PRFactoryAgentType.ClaudeCode, Model = "opus", Effort = PRFactoryEffort.High, Order = 1 },
                    new PRFactoryTeamMember { Name = "writer", Role = "Write", Backend = PRFactoryAgentType.Codex, Order = 2 }
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
            Assert.Equal(leadSession, request.LeadSessionId);
            if (request.Backend == "claude")
            {
                Assert.Equal("opus", request.Model);
                Assert.Equal("high", request.Effort);
            }
            var id = "job_" + spawns;
            jobs[id] = NewJob(id, request.Backend!);
            return JobResult.Ok(new JobView(id, JobStatus.Queued, null, null, 0), "accepted");
        }
        PRFactoryWorkItems Adapter() => new("https://example.test", [new RepositoryMapping(server.Item.RepositoryId, dir.Path)],
            teams, new PRFactoryClient(PRFactoryClient.CreateHttpClient("https://example.test", "token", new FakeHandler(server.Reply))),
            Submit, id => jobs.GetValueOrDefault(id), () => { }, cwd => sessions.Start(cwd, "prfactory:https://example.test").SessionId);
        var first = Adapter();
        await first.TickAsync(null, CancellationToken.None);
        Assert.Equal(2, spawns);
        Assert.Equal(0, server.Uploads);
        foreach (var id in jobs.Keys.ToArray())
        {
            jobs[id] = jobs[id] with { Status = JobStatus.Completed, ResultText = "Done" };
        }

        var restarted = Adapter();
        await restarted.TickAsync(null, CancellationToken.None);
        Assert.Equal(3, spawns); // The recipe admits one child at a time.
        Assert.Equal(0, server.Uploads);
        jobs["job_3"] = jobs["job_3"] with { Status = JobStatus.Completed, ResultText = "Done" };
        await restarted.TickAsync(null, CancellationToken.None); // Poll offers the same item again.
        Assert.Equal(3, spawns);
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
            Id = Guid.NewGuid(),
            RepositoryId = Guid.NewGuid(),
            Prompt = "Do work",
            ContextJson = "{\"repositories\":{\"secondary\":[{\"id\":\"" + Guid.NewGuid().ToString("D") + "\"}]}}"
        });
        var spawns = 0;
        var adapter = new PRFactoryWorkItems("https://example.test", [new RepositoryMapping(server.Item.RepositoryId, dir.Path)],
            teams, new PRFactoryClient(PRFactoryClient.CreateHttpClient("https://example.test", "token", new FakeHandler(server.Reply))),
            _ => { spawns++; throw new InvalidOperationException("should not submit"); }, _ => null, () => { });
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
                return Json("{\"workItems\":[" + JsonSerializer.Serialize(Item, PRFactoryWorkItemJson.Default.PRFactoryWorkItem) + "]}");
            }
            if (path.Contains("/claim/", StringComparison.Ordinal))
            {
                Claims++;
                Calls.Add("claim");
                return Json("{\"workItem\":" + JsonSerializer.Serialize(Item, PRFactoryWorkItemJson.Default.PRFactoryWorkItem) + "}");
            }
            if (path.Contains("/artefacts/", StringComparison.Ordinal))
            {
                Uploads++;
                Calls.Add("artefacts");
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var json = JsonDocument.Parse(body);
                var artefacts = json.RootElement.GetProperty("artefacts");
                UploadContent = artefacts.GetArrayLength() > 0 ? artefacts[0].GetProperty("content").GetString() : null;
                return Json("{\"accepted\":true}");
            }
            if (path.Contains("/complete/", StringComparison.Ordinal))
            {
                Completions++;
                Calls.Add("complete");
                return Json("{\"accepted\":true}");
            }
            if (path.Contains("/fail/", StringComparison.Ordinal))
            {
                Calls.Add("fail");
                return Json("{\"acknowledged\":true}");
            }
            throw new InvalidOperationException(path);
        }

        static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }

    sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }
}
