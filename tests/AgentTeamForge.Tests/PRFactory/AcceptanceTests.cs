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

public sealed class AcceptanceTests
{
    static readonly Guid Machine = Guid.Parse("8ad6f5c0-a4f0-42dc-8c29-59677ea37949");
    const string ServerUrl = "https://example.test";

    [Fact]
    public async Task Lost_ack_retries_same_identity_before_dispatch()
    {
        using var dir = new TempStateDir();
        var teams = new PRFactoryTeamStore(JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2)));
        var server = new FakeServer(NewItem()) { LoseFirstAck = true };
        var dispatched = 0;
        var adapter = Adapter(dir, teams, server, () => dispatched++);

        await adapter.TickAsync(Machine, CancellationToken.None);
        var pending = teams.Get(ServerUrl, server.Item.Id)!;
        Assert.Equal("pending", pending.AcceptanceState);
        Assert.Equal(0, dispatched);
        Assert.Equal(1, server.Posts);
        Assert.Equal(pending.AtfJobId, server.AcceptedJobId);
        Assert.Equal(Machine, pending.MachineId);

        await adapter.TickAsync(Machine, CancellationToken.None);
        Assert.Equal("accepted", teams.Get(ServerUrl, server.Item.Id)!.AcceptanceState);
        Assert.Equal(2, server.Posts);
        Assert.Equal(pending.AtfJobId, server.AcceptedJobId);
        Assert.Equal(1, dispatched);
    }

    [Fact]
    public async Task Restart_reads_server_acceptance_before_resuming_local_team()
    {
        using var dir = new TempStateDir();
        var path = dir.File("jobs.db");
        var teams = new PRFactoryTeamStore(JobDatabase.Create(path, TimeSpan.FromSeconds(2)));
        var server = new FakeServer(NewItem());
        var dispatched = 0;
        await Adapter(dir, teams, server, () => dispatched++).TickAsync(Machine, CancellationToken.None);
        Assert.Equal(1, server.Posts);
        Assert.Equal(1, dispatched);

        var reopened = new PRFactoryTeamStore(JobDatabase.Open(path, TimeSpan.FromSeconds(2)));
        await Adapter(dir, reopened, server, () => dispatched++).TickAsync(Machine, CancellationToken.None);
        Assert.Equal(1, server.Posts);
        Assert.True(server.Gets >= 1);
        Assert.Equal(1, dispatched);
        Assert.Equal("accepted", reopened.Get(ServerUrl, server.Item.Id)!.AcceptanceState);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Lost_authority_fences_publication_and_keeps_local_result(HttpStatusCode status)
    {
        using var dir = new TempStateDir();
        var teams = new PRFactoryTeamStore(JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2)));
        var server = new FakeServer(NewItem());
        var job = NewJob() with { Status = JobStatus.Running };
        var adapter = Adapter(dir, teams, server, () => { }, () => job);
        await adapter.TickAsync(Machine, CancellationToken.None);
        job = job with { Status = JobStatus.Completed, ResultText = "local result" };
        server.GetOverride = status;

        await adapter.TickAsync(Machine, CancellationToken.None);
        Assert.Equal("reconciliation_needed", teams.Get(ServerUrl, server.Item.Id)!.AcceptanceState);
        Assert.Equal("local result", job.ResultText);
        Assert.Equal(0, server.Uploads);
        Assert.Equal(0, server.Completions);
        var calls = server.Gets;
        await adapter.TickAsync(Machine, CancellationToken.None);
        Assert.Equal(calls, server.Gets);
        Assert.Contains(teams.ReconciliationNeeded(ServerUrl), t => t.WorkItemId == server.Item.Id);
        var state = StateDirectory.Open(dir.Path);
        PRFactoryConnection.PublishJoinTickets(state, teams, ServerUrl);
        Assert.Contains("reconciliation needed", File.ReadAllText(dir.File("prfactory-joins.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acceptance_conflict_fences_before_any_job_dispatch()
    {
        using var dir = new TempStateDir();
        var teams = new PRFactoryTeamStore(JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2)));
        var server = new FakeServer(NewItem()) { PostOverride = HttpStatusCode.Conflict };
        var dispatched = 0;
        await Adapter(dir, teams, server, () => dispatched++).TickAsync(Machine, CancellationToken.None);
        Assert.Equal(0, dispatched);
        Assert.Equal("reconciliation_needed", teams.Get(ServerUrl, server.Item.Id)!.AcceptanceState);
    }

    [Fact]
    public async Task Server_reconciliation_status_fences_existing_local_job()
    {
        using var dir = new TempStateDir();
        var teams = new PRFactoryTeamStore(JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2)));
        var server = new FakeServer(NewItem());
        var job = NewJob() with { Status = JobStatus.Running };
        var adapter = Adapter(dir, teams, server, () => { }, () => job);
        server.Status = 1;
        await adapter.TickAsync(Machine, CancellationToken.None);
        job = job with { Status = JobStatus.Completed, ResultText = "kept" };
        server.Status = 6;
        await adapter.TickAsync(Machine, CancellationToken.None);
        Assert.Equal("reconciliation_needed", teams.Get(ServerUrl, server.Item.Id)!.AcceptanceState);
        Assert.Equal("kept", job.ResultText);
        Assert.Equal(0, server.Uploads);
    }

    [Fact]
    public async Task Server_without_acceptance_routes_uses_legacy_lease_and_logs_once()
    {
        using var dir = new TempStateDir();
        var teams = new PRFactoryTeamStore(JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2)));
        var server = new FakeServer(NewItem()) { EndpointMissing = true };
        var logs = new List<string>();
        var dispatched = 0;
        var adapter = Adapter(dir, teams, server, () => dispatched++, log: logs.Add);

        await adapter.TickAsync(Machine, CancellationToken.None);
        await adapter.TickAsync(Machine, CancellationToken.None);
        Assert.Equal("legacy", teams.Get(ServerUrl, server.Item.Id)!.AcceptanceState);
        Assert.Equal(1, dispatched);
        Assert.Equal(1, server.Posts);
        Assert.Single(logs, line => line.Contains("legacy lease", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_work_item_is_fenced_instead_of_legacy_dispatch()
    {
        using var dir = new TempStateDir();
        var teams = new PRFactoryTeamStore(JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2)));
        var server = new FakeServer(NewItem()) { EndpointMissing = true, LeaseLost = true };
        var dispatched = 0;
        await Adapter(dir, teams, server, () => dispatched++).TickAsync(Machine, CancellationToken.None);
        Assert.Equal(0, dispatched);
        Assert.Equal("reconciliation_needed", teams.Get(ServerUrl, server.Item.Id)!.AcceptanceState);
    }

    static PRFactoryWorkItem NewItem() => new()
    {
        Id = Guid.NewGuid(),
        RepositoryId = Guid.NewGuid(),
        LeaseToken = Guid.NewGuid(),
        AgentType = PRFactoryAgentType.Codex,
        Prompt = "Do work"
    };

    static JobRecord NewJob() => new("local-job", "prfactory", "connector", "connector-lead", "local-job", "prompt", "",
        JobStatus.Queued, null, null, 0, "codex", null, null, null);

    static PRFactoryWorkItems Adapter(TempStateDir dir, PRFactoryTeamStore teams, FakeServer server,
        Action onSubmit, Func<JobRecord>? job = null, Action<string>? log = null)
    {
        var local = job ?? NewJob;
        var client = new PRFactoryClient(PRFactoryClient.CreateHttpClient(ServerUrl, "fake-token", new FakeHandler(server.Reply)));
        return new PRFactoryWorkItems(ServerUrl, [new RepositoryMapping(server.Item.RepositoryId, dir.Path)], teams, client,
            _ => { onSubmit(); return JobResult.Ok(new JobView("local-job", JobStatus.Queued, null, null, 0), "accepted"); },
            _ => local(), () => { }, log: log);
    }

    sealed class FakeServer(PRFactoryWorkItem item)
    {
        public PRFactoryWorkItem Item { get; } = item;
        public bool LoseFirstAck { get; set; }
        public bool EndpointMissing { get; set; }
        public bool LeaseLost { get; set; }
        public HttpStatusCode? GetOverride { get; set; }
        public HttpStatusCode? PostOverride { get; set; }
        public int Status { get; set; } = 1;
        public string? AcceptedJobId { get; private set; }
        public int Posts { get; private set; }
        public int Gets { get; private set; }
        public int Uploads { get; private set; }
        public int Completions { get; private set; }

        public HttpResponseMessage Reply(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/poll", StringComparison.Ordinal))
            {
                return Json("{\"workItems\":[" + JsonSerializer.Serialize(Item, PRFactoryWorkItemJson.Default.PRFactoryWorkItem) + "]}");
            }
            if (path.Contains("/claim/", StringComparison.Ordinal))
            {
                return Json("{\"workItem\":" + JsonSerializer.Serialize(Item, PRFactoryWorkItemJson.Default.PRFactoryWorkItem) + "}");
            }
            if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                return LeaseLost ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json("{\"renewed\":true}");
            }
            if (path.EndsWith("/atf-acceptance", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Get)
                {
                    Gets++;
                    if (EndpointMissing)
                    {
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    }
                    if (GetOverride is { } status)
                    {
                        return new HttpResponseMessage(status);
                    }
                    var query = request.RequestUri.Query;
                    if (AcceptedJobId is null || !query.Contains(Uri.EscapeDataString(AcceptedJobId), StringComparison.Ordinal))
                    {
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    }
                    return Acceptance();
                }
                Posts++;
                if (EndpointMissing)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
                if (PostOverride is { } postStatus)
                {
                    return new HttpResponseMessage(postStatus);
                }
                using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var body = json.RootElement;
                Assert.Equal(Machine, body.GetProperty("machineId").GetGuid());
                Assert.Equal(Item.LeaseToken, body.GetProperty("leaseToken").GetGuid());
                var id = body.GetProperty("jobId").GetString();
                if (AcceptedJobId is not null && AcceptedJobId != id)
                {
                    return new HttpResponseMessage(HttpStatusCode.Conflict);
                }
                AcceptedJobId = id;
                if (LoseFirstAck) { LoseFirstAck = false; throw new IOException("ack lost"); }
                return Acceptance();
            }
            if (path.Contains("/artefacts/", StringComparison.Ordinal)) { Uploads++; return Json("{\"accepted\":true}"); }
            if (path.Contains("/complete/", StringComparison.Ordinal)) { Completions++; return Json("{\"accepted\":true}"); }
            throw new InvalidOperationException(path);
        }

        HttpResponseMessage Acceptance() => Json("{\"atfJobId\":\"" + AcceptedJobId + "\",\"status\":" + Status + "}");
        static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }
}
