using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Usage;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class DeliveryTests
{
    [Fact]
    public async Task Registration_poll_and_claim_advertise_single_repository_contract_version()
    {
        using var f = new Fixture();
        await f.Client.RegisterMachineAsync(CancellationToken.None);
        await f.Adapter().TickAsync(null, CancellationToken.None);
        Assert.Equal(["1.0.0", "1.0.0", "1.0.0"], f.Versions);
    }

    [Fact]
    public async Task Managed_output_replays_exact_lost_batch_then_drains_before_completion()
    {
        using var f = new Fixture();
        await f.Adapter().TickAsync(null, CancellationToken.None);
        var id = f.Teams.MemberJob(Fixture.Url, f.Item.Id, "lead", 0)!;
        var sink = f.Logs.BeginRun(id, "run", "codex");
        sink("stdout", Encoding.UTF8.GetBytes(new string('x', 18000) + "\n"));
        f.LoseStreamReply = true;
        await f.Adapter().TickAsync(null, CancellationToken.None);
        var lost = f.Bodies[^1];
        f.Finish(id, new string('r', 20000));
        await f.Adapter().TickAsync(null, CancellationToken.None);
        Assert.Equal(lost, f.Bodies[^1]);
        Assert.Null(f.Completion);
        for (var i = 0; i < 10 && f.Completion is null; i++) { await f.Adapter().TickAsync(null, CancellationToken.None); }
        Assert.NotNull(f.Completion);
        Assert.Contains(new string('x', 18000), string.Concat(f.Lines.Where(l => l.RecordKind == "job-output").Select(l => l.Text)));
        Assert.Equal(new string('r', 20000), string.Concat(f.Lines.Where(l => l.RecordKind == "job-result").Select(l => l.Text)));
        Assert.Equal(f.Lines.Count, f.Lines.Select(l => l.Seq).Distinct().Count());
    }

    [Fact]
    public async Task Managed_send_is_deferred_deduplicated_and_kill_stops_all_turns()
    {
        using var f = new Fixture();
        await f.Adapter().TickAsync(null, CancellationToken.None);
        var parent = f.Teams.MemberJob(Fixture.Url, f.Item.Id, "lead", 0)!;
        var send = new PRFactoryCommand(Guid.NewGuid(), "SendMessage", "lead", "follow up");
        f.Commands.Add(send);
        f.LoseAckReply = true;
        await f.Adapter().TickAsync(null, CancellationToken.None);
        var follow = Assert.Single(f.Jobs.ListJobs("prfactory", "connector", 10), j => j.ParentJobId == parent);
        Assert.Equal(JobStatus.Queued, follow.Status);
        await f.Adapter().TickAsync(null, CancellationToken.None);
        Assert.Equal(2, f.Jobs.ListJobs("prfactory", "connector", 10).Count);
        Assert.True(f.Acks[send.CommandId].Accepted);
        var pendingSend = new PRFactoryCommand(Guid.NewGuid(), "SendMessage", "lead", "must not restart after kill");
        f.Commands.Add(pendingSend);
        f.Commands.Add(new(Guid.NewGuid(), "KillAgent", "lead", null));
        await f.Adapter().TickAsync(null, CancellationToken.None);
        Assert.Equal(2, f.Jobs.ListJobs("prfactory", "connector", 10).Count);
        Assert.False(f.Acks[pendingSend.CommandId].Accepted);
        Assert.All(f.Jobs.ListJobs("prfactory", "connector", 10), j => Assert.Equal(JobStatus.Cancelled, j.Status));
        Assert.Equal("failed", f.Teams.Get(Fixture.Url, f.Item.Id)!.State);
    }

    [Fact]
    public async Task Message_after_kill_of_idle_member_does_not_resume_its_session()
    {
        using var f = new Fixture();
        await f.Adapter().TickAsync(null, CancellationToken.None);
        var id = f.Teams.MemberJob(Fixture.Url, f.Item.Id, "lead", 0)!;
        f.Finish(id, "done");
        var send = new PRFactoryCommand(Guid.NewGuid(), "SendMessage", "lead", "must not restart after kill");
        f.Commands.Add(new(Guid.NewGuid(), "KillAgent", "lead", null));
        f.Commands.Add(send);
        await f.Adapter().TickAsync(null, CancellationToken.None);
        Assert.Single(f.Jobs.ListJobs("prfactory", "connector", 10));
        Assert.Equal("member_closed", f.Acks[send.CommandId].Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completion_uses_actual_worktree_branch_and_head_only_when_writable(bool readOnly)
    {
        using var f = new Fixture();
        Git(f.Dir.Path, "init");
        Git(f.Dir.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        f.Item.ReadOnly = readOnly;
        await f.Adapter().TickAsync(null, CancellationToken.None);
        var id = f.Teams.MemberJob(Fixture.Url, f.Item.Id, "lead", 0)!;
        var job = f.Jobs.GetJob(id)!;
        Assert.Equal(!readOnly, job.WorktreePath is not null);
        Assert.True(JobWorktree.Prepare(job, TestContext.Current.CancellationToken));
        var cwd = JobWorktree.WorkingDirectory(job)!;
        Git(cwd, "checkout", "-b", "implementation-result");
        Git(cwd, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "result");
        f.Finish(id, "done");
        await f.Adapter().TickAsync(null, CancellationToken.None);
        Assert.NotNull(f.Completion);
        Assert.Equal(readOnly ? null : "implementation-result", f.Completion.ResultBranch);
        Assert.Equal(readOnly ? null : JobWorktree.Head(cwd), f.Completion.ResultCommitSha);
    }

    [Theory]
    [InlineData(PRFactoryAgentType.ClaudeCode)]
    [InlineData(PRFactoryAgentType.Codex)]
    public async Task Completion_carries_usage_read_from_the_session_it_ran(PRFactoryAgentType agent)
    {
        using var f = new Fixture();
        f.Item.AgentType = agent;
        await f.Adapter().TickAsync(null, CancellationToken.None);
        var id = f.Teams.MemberJob(Fixture.Url, f.Item.Id, "lead", 0)!;
        var home = f.Dir.File("agent-home");
        if (agent == PRFactoryAgentType.Codex)
        {
            var dir = Directory.CreateDirectory(Path.Combine(home, "sessions", "test")).FullName;
            File.WriteAllText(Path.Combine(dir, "rollout-test-thread.jsonl"), "{\"type\":\"session_meta\",\"payload\":{\"id\":\"thread\",\"source\":\"cli\"}}\n"
                + "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":200,\"cached_input_tokens\":80,\"output_tokens\":30}}}}\n");
        }
        else
        {
            var dir = Directory.CreateDirectory(Path.Combine(home, "projects", "test")).FullName;
            File.WriteAllText(Path.Combine(dir, "thread.jsonl"),
                "{\"type\":\"assistant\",\"sessionId\":\"thread\",\"isSidechain\":false,\"message\":{\"id\":\"m1\",\"usage\":{\"input_tokens\":10,\"output_tokens\":2,\"cache_read_input_tokens\":3,\"cache_creation_input_tokens\":4}}}\n"
                + "{\"type\":\"assistant\",\"sessionId\":\"thread\",\"isSidechain\":false,\"message\":{\"id\":\"m2\",\"usage\":{\"input_tokens\":10,\"output_tokens\":2,\"cache_read_input_tokens\":3,\"cache_creation_input_tokens\":4}}}\n");
        }
        f.Finish(id, "done", session: "thread");
        await f.Adapter(j => SessionTokenUsage.ReadFinal(j.Backend, j.SessionId!, home, j.Cwd)).TickAsync(null, CancellationToken.None);
        Assert.Equal(agent == PRFactoryAgentType.Codex
            ? new PRFactoryUsageReport(PRFactoryAgentType.Codex, null, 120, 30, 80, null)
            : new PRFactoryUsageReport(PRFactoryAgentType.ClaudeCode, null, 20, 4, 6, 8), f.Completion!.Usage! with { Model = null });
        Assert.Equal(JobOptions.Read(f.Jobs.GetJob(id)!.Options, "model"), f.Completion.Usage.Model);
    }

    static void Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) { start.ArgumentList.Add(arg); }
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    sealed class Fixture : IDisposable
    {
        public const string Url = "https://example.test";
        public TempStateDir Dir { get; } = new();
        public JobStore Jobs { get; }
        public PRFactoryTeamStore Teams { get; }
        public JobLogs Logs { get; }
        public PRFactoryClient Client { get; }
        readonly AcceptJob accept;
        readonly FollowUpJob follow;
        readonly StopJob stop;
        public PRFactoryWorkItem Item { get; } = new() { Id = Guid.NewGuid(), RepositoryId = Guid.NewGuid(), LeaseToken = Guid.NewGuid(), AgentType = PRFactoryAgentType.Codex, ReadOnly = true, Prompt = "work" };
        public List<string> Versions { get; } = [];
        public List<string> Bodies { get; } = [];
        public List<PRFactoryStreamLine> Lines { get; } = [];
        public List<PRFactoryCommand> Commands { get; } = [];
        public Dictionary<Guid, PRFactoryCommandAck> Acks { get; } = [];
        public PRFactoryCompletionRequest? Completion { get; private set; }
        public bool LoseStreamReply { get; set; }
        public bool LoseAckReply { get; set; }
        readonly HttpClient http;
        public Fixture()
        {
            var db = JobDatabase.Create(Dir.File("jobs.db"), TimeSpan.FromSeconds(2));
            Jobs = new(db, DurabilityCheckpoints.None);
            Teams = new(db);
            Logs = new(Dir.Path);
            var principal = new BoundPrincipal("prfactory", "connector", "connector-lead");
            accept = new(Jobs, principal, new SpikeLimits(), false, new AdmissionGate(), ["codex", "claude"]);
            follow = new(Jobs, principal, accept);
            stop = new(Jobs, principal, _ => { });
            http = PRFactoryClient.CreateHttpClient(Url, "token", new Handler(Reply));
            Client = new(http);
        }
        public PRFactoryWorkItems Adapter(Func<JobRecord, TokenUsage?>? sessionUsage = null) => new(Url, [new(Item.RepositoryId!.Value, Dir.Path)], Teams, Client,
            accept.Execute, Jobs.GetJob, () => { }, stopJob: stop.Execute, followUp: follow.Execute, jobLogs: Logs, sessionUsage: sessionUsage);
        public void Finish(string id, string result, string? session = null)
        {
            var run = Jobs.BeginNextAttempt()!;
            Assert.Equal(id, run.Job.JobId);
            if (session is not null) { Assert.True(Jobs.RecordSession(new(id, run.RunId, run.Generation, run.Correlation), session)); }
            Assert.True(Jobs.Complete(new(id, run.RunId, run.Generation, run.Correlation), result));
        }
        HttpResponseMessage Reply(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/work-item-blobs/capabilities") { return new(HttpStatusCode.NotFound); }
            if (path == "/api/worker/capabilities") { return new(HttpStatusCode.NotFound); }
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            if (path.EndsWith("/register", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(body!);
                Versions.Add(doc.RootElement.GetProperty("workerVersion").GetString()!);
                return Json("{\"machineId\":\"" + Guid.NewGuid() + "\",\"heartbeatIntervalSeconds\":1}");
            }
            if (path.EndsWith("/poll", StringComparison.Ordinal))
            {
                Versions.Add(request.RequestUri.Query.Split("workerVersion=")[1].Split('&')[0]);
                return Json("{\"workItems\":[" + JsonSerializer.Serialize(Item, PRFactoryWorkItemJson.Default.PRFactoryWorkItem) + "]}");
            }
            if (path.Contains("/claim/", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(body!);
                Versions.Add(doc.RootElement.GetProperty("workerVersion").GetString()!);
                return Json("{\"workItem\":" + JsonSerializer.Serialize(Item, PRFactoryWorkItemJson.Default.PRFactoryWorkItem) + "}");
            }
            if (path.EndsWith("/agent-commands", StringComparison.Ordinal))
            {
                return Json(JsonSerializer.Serialize(new PRFactoryCommandDrainResponse([.. Commands.Where(c => !Acks.ContainsKey(c.CommandId))]), PRFactoryWorkItemJson.Default.PRFactoryCommandDrainResponse));
            }
            if (path.EndsWith("/agent-commands/ack", StringComparison.Ordinal))
            {
                var acks = JsonSerializer.Deserialize(body!, PRFactoryWorkItemJson.Default.PRFactoryCommandAckRequest)!;
                foreach (var ack in acks.Acks) { Acks[ack.CommandId] = ack; }
                if (LoseAckReply) { LoseAckReply = false; throw new IOException("lost ack"); }
                return Json("{\"applied\":1}");
            }
            if (path.EndsWith("/agent-stream", StringComparison.Ordinal))
            {
                Bodies.Add(body!);
                var batch = JsonSerializer.Deserialize(body!, PRFactoryWorkItemJson.Default.PRFactoryStreamBatch)!;
                foreach (var line in batch.Lines)
                {
                    if (!Lines.Any(l => l.AgentName == line.AgentName && l.Seq == line.Seq)) { Lines.Add(line); }
                }
                if (LoseStreamReply) { LoseStreamReply = false; throw new IOException("lost stream response"); }
                return ManagedWire.Reply(request)!;
            }
            if (path.Contains("/complete/", StringComparison.Ordinal))
            {
                Completion = JsonSerializer.Deserialize(body!, PRFactoryWorkItemJson.Default.PRFactoryCompletionRequest);
                return Json("{\"accepted\":true}");
            }
            if (path.Contains("/artefacts/", StringComparison.Ordinal)) { return Json("{\"accepted\":true}"); }
            if (path.Contains("/fail/", StringComparison.Ordinal)) { return Json("{\"acknowledged\":true}"); }
            throw new InvalidOperationException(path);
        }
        static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        public void Dispose() { http.Dispose(); Dir.Dispose(); }
    }
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }
}
