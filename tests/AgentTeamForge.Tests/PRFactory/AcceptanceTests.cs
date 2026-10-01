using System.Net;
using System.Text;
using System.Text.Json;
using AgentTeamForge.Business.Features.Agents.Backends;
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
        // Fenced teams keep observing the server only for a definitive disposition; nothing is published.
        await adapter.TickAsync(Machine, CancellationToken.None);
        Assert.Equal(0, server.Uploads + server.Completions);
        Assert.Equal("reconciliation_needed", teams.Get(ServerUrl, server.Item.Id)!.AcceptanceState);
        Assert.Contains(teams.ReconciliationNeeded(ServerUrl), t => t.WorkItemId == server.Item.Id);
        teams.RecordExternal(ServerUrl, server.Item.Id, "reviewer", "reviewer", "team-1", "secret-ticket", DateTimeOffset.UtcNow.AddHours(1));
        var state = StateDirectory.Open(dir.Path);
        PRFactoryConnection.PublishJoinTickets(state, teams, ServerUrl);
        var snapshot = File.ReadAllText(dir.File("prfactory-joins.json"));
        Assert.Contains("reconciliation needed", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("external member reviewer", snapshot, StringComparison.Ordinal);

        // Once the server confirms the item terminal and the local team is closed, the fence notice drops out.
        teams.Finish(ServerUrl, server.Item.Id, "failed");
        Assert.Empty(teams.ReconciliationNeeded(ServerUrl));
        PRFactoryConnection.PublishJoinTickets(state, teams, ServerUrl);
        Assert.DoesNotContain("reconciliation needed", File.ReadAllText(dir.File("prfactory-joins.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Polling_an_already_fenced_item_does_not_log_the_fence_again()
    {
        using var dir = new TempStateDir();
        var teams = new PRFactoryTeamStore(JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2)));
        var server = new FakeServer(NewItem());
        var logs = new List<string>();
        var adapter = Adapter(dir, teams, server, () => { }, log: logs.Add);
        server.Status = 1;
        await adapter.TickAsync(Machine, CancellationToken.None);
        server.Status = 6;
        for (var i = 0; i < 3; i++) { await adapter.TickAsync(Machine, CancellationToken.None); }
        Assert.Equal("reconciliation_needed", teams.Get(ServerUrl, server.Item.Id)!.AcceptanceState);
        Assert.Single(logs, l => l.Contains("reconciliation needed", StringComparison.Ordinal));
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

    [Fact]
    public async Task Release_refuses_an_active_or_unknown_team()
    {
        using var dir = new TempStateDir();
        var teams = new PRFactoryTeamStore(JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2)));
        var server = new FakeServer(NewItem()) { Status = 1 };
        await Adapter(dir, teams, server, () => { }).TickAsync(Machine, CancellationToken.None);
        Assert.Equal("accepted", teams.Get(ServerUrl, server.Item.Id)!.AcceptanceState);
        Assert.Equal(PRFactoryReleaseResult.NotFenced, teams.ReleaseFenced(ServerUrl, server.Item.Id));
        Assert.NotNull(teams.Get(ServerUrl, server.Item.Id));
        Assert.Equal(PRFactoryReleaseResult.NotFound, teams.ReleaseFenced(ServerUrl, Guid.NewGuid()));
    }

    [Fact]
    public async Task Release_forgets_a_fenced_team_and_the_next_poll_claims_it_again()
    {
        using var dir = new TempStateDir();
        var database = JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2));
        var teams = new PRFactoryTeamStore(database);
        var server = new FakeServer(NewItem());
        var adapter = Adapter(dir, teams, server, () => { });
        server.Status = 1;
        await adapter.TickAsync(Machine, CancellationToken.None);
        server.Status = 6;
        await adapter.TickAsync(Machine, CancellationToken.None);
        Assert.Equal("reconciliation_needed", teams.Get(ServerUrl, server.Item.Id)!.AcceptanceState);
        teams.RecordExternal(ServerUrl, server.Item.Id, "reviewer", "reviewer", "team-1", "t", DateTimeOffset.UtcNow.AddHours(1));
        var authority = new PRFactoryAuthorityStore(database);
        teams.MarkExternalClosed(ServerUrl, server.Item.Id);
        authority.Set(ServerUrl, server.Item.Id, "accepted", null);
        authority.Set(ServerUrl, server.Item.Id, "reconciliation-needed", "test");
        authority.Stopped(ServerUrl, server.Item.Id);
        Assert.NotEmpty(authority.Read(ServerUrl));

        var oldJob = teams.Get(ServerUrl, server.Item.Id)!.AtfJobId;
        Assert.Equal(PRFactoryReleaseResult.Released, teams.ReleaseFenced(ServerUrl, server.Item.Id));
        Assert.Null(teams.Get(ServerUrl, server.Item.Id));
        Assert.Empty(authority.Read(ServerUrl));
        // A stale fenced poll racing the release must not resurrect the authority row.
        authority.Set(ServerUrl, server.Item.Id, "reconciliation-needed", "late");
        Assert.Empty(authority.Read(ServerUrl));

        server.Status = 1;
        await adapter.TickAsync(Machine, CancellationToken.None);
        var reclaimed = teams.Get(ServerUrl, server.Item.Id);
        Assert.NotNull(reclaimed);
        Assert.NotEqual(oldJob, reclaimed.AtfJobId); // A fresh claim with its own acceptance identity.
    }

    [Fact]
    public async Task Release_fails_closed_until_the_stop_is_acknowledged_and_nothing_is_uncertain()
    {
        using var dir = new TempStateDir();
        using var jobs = new JobFixture();
        var teams = new PRFactoryTeamStore(jobs.Database);
        var authority = new PRFactoryAuthorityStore(jobs.Database);
        var server = new FakeServer(NewItem());
        var adapter = RealJobsAdapter(dir, teams, server, jobs);
        var id = server.Item.Id;
        server.Status = 1;
        await adapter.TickAsync(Machine, CancellationToken.None);
        var lead = teams.MemberJob(ServerUrl, id, "lead", 0)!;
        // Fenced, but the authority write has not happened yet (SetAcceptance-to-Observe gap).
        teams.SetAcceptance(ServerUrl, id, "reconciliation_needed");
        MarkJob(jobs, lead, "completed"); // e.g. a completed job that still owns a retained session
        Assert.Equal(PRFactoryReleaseResult.StopNotAcknowledged, teams.ReleaseFenced(ServerUrl, id));
        authority.Set(ServerUrl, id, "accepted", null);
        Assert.Equal(PRFactoryReleaseResult.StopNotAcknowledged, teams.ReleaseFenced(ServerUrl, id));
        authority.Set(ServerUrl, id, "reconciliation-needed", "gap"); // stopping=1 until the daemon acknowledges
        Assert.Equal(PRFactoryReleaseResult.StopNotAcknowledged, teams.ReleaseFenced(ServerUrl, id));
        authority.Stopped(ServerUrl, id);
        MarkJob(jobs, lead, "needs_reconciliation");
        Assert.Equal(PRFactoryReleaseResult.NeedsReconciliation, teams.ReleaseFenced(ServerUrl, id));
        MarkJob(jobs, lead, "completed");
        teams.RecordExternal(ServerUrl, id, "reviewer", "reviewer", "team-1", "t", DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(PRFactoryReleaseResult.ExternalMemberOpen, teams.ReleaseFenced(ServerUrl, id));
        teams.MarkExternalClosed(ServerUrl, id);
        Assert.Equal(PRFactoryReleaseResult.Released, teams.ReleaseFenced(ServerUrl, id));
    }

    [Fact]
    public async Task Reclaim_after_release_starts_a_fresh_job_and_drops_old_pending_commands()
    {
        using var dir = new TempStateDir();
        using var jobs = new JobFixture();
        var teams = new PRFactoryTeamStore(jobs.Database);
        var authority = new PRFactoryAuthorityStore(jobs.Database);
        var server = new FakeServer(NewItem());
        var adapter = RealJobsAdapter(dir, teams, server, jobs);
        var id = server.Item.Id;
        server.Status = 1;
        await adapter.TickAsync(Machine, CancellationToken.None);
        var oldLead = teams.MemberJob(ServerUrl, id, "lead", 0)!;
        // A stable key within one acceptance: another tick maps the same job.
        await adapter.TickAsync(Machine, CancellationToken.None);
        Assert.Equal(oldLead, teams.MemberJob(ServerUrl, id, "lead", 0));

        server.Status = 6;
        await adapter.TickAsync(Machine, CancellationToken.None);
        MarkJob(jobs, oldLead, "failed");
        authority.Set(ServerUrl, id, "accepted", null);
        authority.Set(ServerUrl, id, "reconciliation-needed", "test");
        authority.Stopped(ServerUrl, id);
        teams.SavePendingCommand(ServerUrl, id, Guid.NewGuid(), "{}", oldLead);
        Assert.Equal(PRFactoryReleaseResult.Released, teams.ReleaseFenced(ServerUrl, id));
        Assert.Empty(teams.PendingCommands(ServerUrl, id));

        server.Status = 1;
        server.AcceptedJobId = null;
        await adapter.TickAsync(Machine, CancellationToken.None);
        var newLead = teams.MemberJob(ServerUrl, id, "lead", 0);
        Assert.NotNull(newLead);
        Assert.NotEqual(oldLead, newLead);
        Assert.Equal(JobStatus.Queued, jobs.Store.GetJob(newLead)!.Status);
        Assert.Equal(JobStatus.Failed, jobs.Store.GetJob(oldLead)!.Status); // Old results are kept untouched.
    }

    static void MarkJob(JobFixture jobs, string jobId, string status)
    {
        using var connection = jobs.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET status=$status WHERE job_id=$id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$id", jobId);
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task Status_hints_how_to_release_a_fenced_team()
    {
        using var dir = new TempStateDir();
        var teams = new PRFactoryTeamStore(JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2)));
        var server = new FakeServer(NewItem());
        var adapter = Adapter(dir, teams, server, () => { });
        server.Status = 1;
        await adapter.TickAsync(Machine, CancellationToken.None);
        server.Status = 6;
        await adapter.TickAsync(Machine, CancellationToken.None);
        PRFactoryConnection.PublishJoinTickets(StateDirectory.Open(dir.Path), teams, ServerUrl);
        Assert.Contains($"atf prfactory release {server.Item.Id:D}", File.ReadAllText(dir.File("prfactory-joins.json")), StringComparison.Ordinal);
    }

    static PRFactoryWorkItem NewItem() => new()
    {
        Id = Guid.NewGuid(),
        RepositoryId = Guid.NewGuid(),
        ReadOnly = true,
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
        return new PRFactoryWorkItems(ServerUrl, [new RepositoryMapping(server.Item.RepositoryId!.Value, dir.Path)], teams, client,
            _ => { onSubmit(); return JobResult.Ok(new JobView("local-job", JobStatus.Queued, null, null, 0), "accepted"); },
            _ => local(), () => { }, log: log);
    }

    static PRFactoryWorkItems RealJobsAdapter(TempStateDir dir, PRFactoryTeamStore teams, FakeServer server, JobFixture jobs)
    {
        var accept = new AcceptJob(jobs.Store, JobFixture.Operator, jobs.Limits, jobs.TestProfile, jobs.Admission,
            [BackendCatalog.Fake, BackendCatalog.Codex]);
        var client = new PRFactoryClient(PRFactoryClient.CreateHttpClient(ServerUrl, "fake-token", new FakeHandler(server.Reply)));
        return new PRFactoryWorkItems(ServerUrl, [new RepositoryMapping(server.Item.RepositoryId!.Value, dir.Path)], teams, client,
            accept.Execute, jobs.Store.GetJob, () => { });
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
        public string? AcceptedJobId { get; set; }
        public int Posts { get; private set; }
        public int Gets { get; private set; }
        public int Uploads { get; private set; }
        public int Completions { get; private set; }

        public HttpResponseMessage Reply(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (ManagedWire.Reply(request) is { } managed) { return managed; }
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
