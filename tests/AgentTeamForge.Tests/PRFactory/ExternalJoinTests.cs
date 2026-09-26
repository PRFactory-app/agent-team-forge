using System.Net;
using System.Text;
using System.Text.Json;
using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.External;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.External;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class ExternalJoinTests
{
    [Fact]
    public async Task External_member_ticket_prompt_reply_restart_and_kill_are_durable()
    {
        using var dir = new TempStateDir();
        using var home = new TempStateDir();
        var sessionsDir = home.File("sessions");
        Directory.CreateDirectory(sessionsDir);
        var thread = Guid.NewGuid().ToString("D");
        File.WriteAllText(Path.Combine(sessionsDir, "rollout-test-" + thread + ".jsonl"), "");
        var database = JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2));
        var server = new FakeServer();
        var submitCount = 0;
        JobResult Submit(SubmitJobRequest request)
        {
            submitCount++;
            return JobResult.Ok(new JobView("lead-job", JobStatus.Running, null, null, 0), "accepted");
        }
        var lead = new JobRecord("lead-job", "prfactory", "connector", "connector-lead", "lead-job", "prompt", "",
            JobStatus.Running, null, null, 0, "codex", null, null, null);
        PRFactoryWorkItems Adapter(JobDatabase db) => new("https://example.test",
            [new RepositoryMapping(server.Item.RepositoryId, dir.Path, ["visitor"])], new PRFactoryTeamStore(db),
            new PRFactoryClient(PRFactoryClient.CreateHttpClient("https://example.test", "token", new FakeHandler(server.Reply))),
            Submit, _ => lead, () => { }, externalTeam: new ExternalTeam(new ExternalMemberStore(db), new WakeStore(db)));

        await Adapter(database).TickAsync(null, CancellationToken.None);
        var store = new PRFactoryTeamStore(database);
        var external = store.External("https://example.test", server.Item.Id, "visitor")!;
        // The tenant-visible stream gets a notice; the bearer ticket stays in the private snapshot.
        Assert.DoesNotContain(external.TicketToken, Assert.Single(server.Lines).Text);
        var state = StateDirectory.Open(dir.Path);
        PRFactoryConnection.PublishJoinTickets(state, store, "https://example.test");
        Assert.Contains(external.TicketToken, Encoding.UTF8.GetString(StateDirectory.ReadPrivateFile(dir.File("prfactory-joins.json"))));
        Assert.Equal(1, submitCount);
        var actor = new ExternalTeam(new ExternalMemberStore(database), new WakeStore(database));
        var command = new PRFactoryCommand(Guid.NewGuid(), "SendMessage", "visitor", "Please review this ticket");
        server.Commands.Add(command);
        await Adapter(database).TickAsync(null, CancellationToken.None);
        Assert.Empty(server.Acknowledged); // The command remains pending until the user joins.

        var token = actor.Join(external.TeamId, external.TicketToken).Member!.MemberToken;
        Assert.NotNull(actor.SetWake(token, thread, home.Path).WakeGeneration);
        await Adapter(database).TickAsync(null, CancellationToken.None);
        Assert.Contains(command.CommandId, server.Acknowledged);
        Assert.Single(new WakeStore(database).PendingExternal());
        Assert.Equal("Please review this ticket", Assert.Single(actor.Read(token, null, null).Inbox!.Messages).Text);
        server.Acknowledged.Remove(command.CommandId); // Simulate command redelivery after a lost ack.
        await Adapter(database).TickAsync(null, CancellationToken.None);
        Assert.Empty(actor.Read(token, null, null).Inbox!.Messages); // No duplicate inbox row.

        Assert.True(actor.Send(token, "My review").Ok);
        await Adapter(database).TickAsync(null, CancellationToken.None);
        Assert.Equal("My review", Assert.Single(server.Lines, l => l.RecordKind == "external-reply").Text);
        var lineCount = server.Lines.Count;
        var postCount = server.StreamPosts;
        var reopened = JobDatabase.Open(dir.File("jobs.db"), TimeSpan.FromSeconds(2));
        await Adapter(reopened).TickAsync(null, CancellationToken.None);
        Assert.Equal(lineCount, server.Lines.Count);
        Assert.Equal(postCount, server.StreamPosts);
        Assert.Equal(1, submitCount);
        Assert.Equal(1, server.Claims);

        var kill = new PRFactoryCommand(Guid.NewGuid(), "KillAgent", "visitor", null);
        server.Commands.Add(kill);
        await Adapter(reopened).TickAsync(null, CancellationToken.None);
        Assert.Contains(kill.CommandId, server.Acknowledged);
        Assert.Equal("membership_revoked", actor.Read(token, null, null).Error);
        Assert.True(new PRFactoryTeamStore(reopened).External("https://example.test", server.Item.Id, "visitor")!.Closed);
    }

    [Fact]
    public async Task Expired_unjoined_ticket_is_reissued_and_the_old_one_stops_working()
    {
        using var dir = new TempStateDir();
        var database = JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2));
        var server = new FakeServer();
        var now = DateTimeOffset.UtcNow;
        var actor = new ExternalTeam(new ExternalMemberStore(database), new WakeStore(database), () => now);
        var lead = new JobRecord("lead-job", "prfactory", "connector", "connector-lead", "lead-job", "prompt", "",
            JobStatus.Running, null, null, 0, "codex", null, null, null);
        var adapter = new PRFactoryWorkItems("https://example.test",
            [new RepositoryMapping(server.Item.RepositoryId, dir.Path, ["visitor"])], new PRFactoryTeamStore(database),
            new PRFactoryClient(PRFactoryClient.CreateHttpClient("https://example.test", "token", new FakeHandler(server.Reply))),
            _ => JobResult.Ok(new JobView("lead-job", JobStatus.Running, null, null, 0), "accepted"), _ => lead, () => { },
            externalTeam: actor);
        var store = new PRFactoryTeamStore(database);

        await adapter.TickAsync(null, CancellationToken.None);
        var first = store.External("https://example.test", server.Item.Id, "visitor")!;
        await adapter.TickAsync(null, CancellationToken.None);
        Assert.Equal(first.TicketToken, store.External("https://example.test", server.Item.Id, "visitor")!.TicketToken);

        now += TimeSpan.FromMinutes(11);
        await adapter.TickAsync(null, CancellationToken.None);
        var renewed = store.External("https://example.test", server.Item.Id, "visitor")!;
        Assert.NotEqual(first.TicketToken, renewed.TicketToken);
        Assert.Equal(first.ActualName, renewed.ActualName);
        Assert.True(renewed.TicketExpires > now);
        Assert.Single(server.Lines); // The notice is not re-published; the private snapshot carries the ticket.
        PRFactoryConnection.PublishJoinTickets(StateDirectory.Open(dir.Path), store, "https://example.test");
        var snapshot = Encoding.UTF8.GetString(StateDirectory.ReadPrivateFile(dir.File("prfactory-joins.json")));
        Assert.Contains(renewed.TicketToken, snapshot);
        Assert.DoesNotContain(first.TicketToken, snapshot);

        Assert.Equal("invalid_or_expired_token", actor.Join(first.TeamId, first.TicketToken).Error);
        Assert.Equal("visitor", actor.Join(renewed.TeamId, renewed.TicketToken).Member!.Name);
        now += TimeSpan.FromMinutes(11);
        await adapter.TickAsync(null, CancellationToken.None);
        Assert.Equal(renewed.TicketToken, store.External("https://example.test", server.Item.Id, "visitor")!.TicketToken);
    }

    [Fact]
    public async Task Managed_jobs_finish_and_revoke_the_external_member()
    {
        using var dir = new TempStateDir();
        var database = JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2));
        var server = new FakeServer();
        server.Item.TeamPlan!.MaxConcurrentChildren = 1;
        server.Item.TeamPlan.Members.Add(new PRFactoryTeamMember { Name = "writer", Role = "Writer", Order = 2 });
        var jobs = new Dictionary<string, JobRecord>();
        JobResult Submit(SubmitJobRequest request)
        {
            var id = "job-" + (jobs.Count + 1);
            jobs[id] = new JobRecord(id, "prfactory", "connector", "connector-lead", id, "prompt", "",
                JobStatus.Running, null, null, 0, "codex", null, null, null);
            return JobResult.Ok(new JobView(id, JobStatus.Running, null, null, 0), "accepted");
        }
        var actor = new ExternalTeam(new ExternalMemberStore(database), new WakeStore(database));
        var store = new PRFactoryTeamStore(database);
        var adapter = new PRFactoryWorkItems("https://example.test",
            [new RepositoryMapping(server.Item.RepositoryId, dir.Path, ["visitor"])], store,
            new PRFactoryClient(PRFactoryClient.CreateHttpClient("https://example.test", "token", new FakeHandler(server.Reply))),
            Submit, id => jobs.GetValueOrDefault(id), () => { }, externalTeam: actor);

        await adapter.TickAsync(null, CancellationToken.None);
        Assert.Equal(2, jobs.Count);
        var external = store.External("https://example.test", server.Item.Id, "visitor")!;
        var token = actor.Join(external.TeamId, external.TicketToken).Member!.MemberToken;
        Assert.True(actor.Send(token, "Final review").Ok);
        foreach (var id in jobs.Keys.ToArray())
        {
            jobs[id] = jobs[id] with { Status = JobStatus.Completed, ResultText = "Done" };
        }

        await adapter.TickAsync(null, CancellationToken.None);
        // Revoked first; a reply racing the close is drained on the next tick.
        Assert.Equal("claimed", store.Get("https://example.test", server.Item.Id)!.State);
        Assert.False(actor.Send(token, "Too late").Ok);
        await adapter.TickAsync(null, CancellationToken.None);
        Assert.Equal("completed", store.Get("https://example.test", server.Item.Id)!.State);
        Assert.Equal("Final review", Assert.Single(server.Lines, l => l.RecordKind == "external-reply").Text);
        Assert.True(server.Calls.IndexOf("external-reply") < server.Calls.IndexOf("complete"));
        Assert.Equal("membership_revoked", actor.Read(token, null, null).Error);
        Assert.True(store.External("https://example.test", server.Item.Id, "visitor")!.Closed);
    }

    [Fact]
    public async Task External_only_item_finishes_after_every_member_leaves_with_final_replies_uploaded()
    {
        using var dir = new TempStateDir();
        var database = JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2));
        var server = new FakeServer();
        server.Item.TeamPlan!.Members.Add(new PRFactoryTeamMember { Name = "second", Role = "Reviewer", Order = 2 });
        var actor = new ExternalTeam(new ExternalMemberStore(database), new WakeStore(database));
        var store = new PRFactoryTeamStore(database);
        var jobs = new JobStore(database, DurabilityCheckpoints.None);
        var principal = new BoundPrincipal("prfactory", "connector", "connector-lead");
        var accept = new AcceptJob(jobs, principal, new SpikeLimits(), false, new AdmissionGate(), ["codex"]);
        var stop = new StopJob(jobs, principal, _ => throw new InvalidOperationException("queued lead must not have a running backend"));
        var adapter = new PRFactoryWorkItems("https://example.test",
            [new RepositoryMapping(server.Item.RepositoryId, dir.Path, ["visitor", "second"])], store,
            new PRFactoryClient(PRFactoryClient.CreateHttpClient("https://example.test", "token", new FakeHandler(server.Reply))),
            accept.Execute, jobs.GetJob, () => { }, externalTeam: actor, stopJob: stop.Execute);

        await adapter.TickAsync(null, CancellationToken.None);
        var first = store.External("https://example.test", server.Item.Id, "visitor")!;
        var second = store.External("https://example.test", server.Item.Id, "second")!;
        var firstToken = actor.Join(first.TeamId, first.TicketToken).Member!.MemberToken;
        var secondToken = actor.Join(second.TeamId, second.TicketToken).Member!.MemberToken;
        Assert.True(actor.Send(firstToken, "First final reply").Ok);
        Assert.True(actor.Leave(firstToken).Ok);
        await adapter.TickAsync(null, CancellationToken.None);
        Assert.Equal("claimed", store.Get("https://example.test", server.Item.Id)!.State);
        Assert.True(actor.Send(secondToken, "Second final reply").Ok);
        Assert.True(actor.Leave(secondToken).Ok);

        await adapter.TickAsync(null, CancellationToken.None);
        Assert.Equal("completed", store.Get("https://example.test", server.Item.Id)!.State);
        var leadId = store.MemberJob("https://example.test", server.Item.Id, "lead", 0)!;
        Assert.Equal(JobStatus.Cancelled, jobs.GetJob(leadId)!.Status);
        Assert.Contains("External members completed", server.CompletionMarkdown);
        Assert.Equal(2, server.Lines.Count(l => l.RecordKind == "external-reply"));
        Assert.Equal("membership_revoked", actor.Read(secondToken, null, null).Error);
        Assert.All(store.ExternalMembers("https://example.test", server.Item.Id), row => Assert.True(row.Closed));
    }

    [Fact]
    public async Task Kill_agent_revokes_only_its_external_member()
    {
        using var dir = new TempStateDir();
        var database = JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2));
        var server = new FakeServer();
        server.Item.TeamPlan!.Members.Add(new PRFactoryTeamMember { Name = "second", Role = "Reviewer", Order = 2 });
        var actor = new ExternalTeam(new ExternalMemberStore(database), new WakeStore(database));
        var store = new PRFactoryTeamStore(database);
        var lead = new JobRecord("lead-job", "prfactory", "connector", "connector-lead", "lead-job", "prompt", "",
            JobStatus.Running, null, null, 0, "codex", null, null, null);
        var adapter = new PRFactoryWorkItems("https://example.test",
            [new RepositoryMapping(server.Item.RepositoryId, dir.Path, ["visitor", "second"])], store,
            new PRFactoryClient(PRFactoryClient.CreateHttpClient("https://example.test", "token", new FakeHandler(server.Reply))),
            _ => JobResult.Ok(new JobView("lead-job", JobStatus.Running, null, null, 0), "accepted"), _ => lead, () => { },
            externalTeam: actor);

        await adapter.TickAsync(null, CancellationToken.None);
        var first = store.External("https://example.test", server.Item.Id, "visitor")!;
        var second = store.External("https://example.test", server.Item.Id, "second")!;
        var firstToken = actor.Join(first.TeamId, first.TicketToken).Member!.MemberToken;
        var secondToken = actor.Join(second.TeamId, second.TicketToken).Member!.MemberToken;
        var kill = new PRFactoryCommand(Guid.NewGuid(), "KillAgent", "visitor", null);
        server.Commands.Add(kill);

        await adapter.TickAsync(null, CancellationToken.None);
        Assert.Contains(kill.CommandId, server.Acknowledged);
        Assert.Equal("membership_revoked", actor.Read(firstToken, null, null).Error);
        Assert.True(actor.Send(secondToken, "Still working").Ok);
        Assert.True(store.External("https://example.test", server.Item.Id, "visitor")!.Closed);
        Assert.False(store.External("https://example.test", server.Item.Id, "second")!.Closed);
        Assert.Equal("claimed", store.Get("https://example.test", server.Item.Id)!.State);
    }

    [Fact]
    public async Task Lost_lease_closes_unjoined_external_team_and_stops_retrying()
    {
        using var dir = new TempStateDir();
        var database = JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2));
        var server = new FakeServer();
        var actor = new ExternalTeam(new ExternalMemberStore(database), new WakeStore(database));
        var store = new PRFactoryTeamStore(database);
        var lead = new JobRecord("lead-job", "prfactory", "connector", "connector-lead", "lead-job", "prompt", "",
            JobStatus.Running, null, null, 0, "codex", null, null, null);
        var stops = new List<string>();
        var adapter = new PRFactoryWorkItems("https://example.test",
            [new RepositoryMapping(server.Item.RepositoryId, dir.Path, ["visitor"])], store,
            new PRFactoryClient(PRFactoryClient.CreateHttpClient("https://example.test", "token", new FakeHandler(server.Reply))),
            _ => JobResult.Ok(new JobView("lead-job", JobStatus.Running, null, null, 0), "accepted"), _ => lead, () => { },
            externalTeam: actor, stopJob: id => { stops.Add(id); lead = lead with { Status = JobStatus.Cancelled }; return JobResult.Ok(new JobView(id, JobStatus.Cancelled, null, null, 0), "stopped"); });

        await adapter.TickAsync(null, CancellationToken.None);
        var external = store.External("https://example.test", server.Item.Id, "visitor")!;
        server.LeaseLost = true;
        await adapter.TickAsync(null, CancellationToken.None);

        Assert.Equal("failed", store.Get("https://example.test", server.Item.Id)!.State);
        Assert.Empty(store.Pending("https://example.test"));
        Assert.Null(actor.Join(external.TeamId, external.TicketToken).Member);
        Assert.True(store.External("https://example.test", server.Item.Id, "visitor")!.Closed);
        Assert.Equal(["lead-job"], stops);
    }

    [Fact]
    public void Revoking_a_member_clears_its_pending_inbox_wake()
    {
        using var dir = new TempStateDir();
        var database = JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2));
        var actor = new ExternalTeam(new ExternalMemberStore(database), new WakeStore(database));
        var teamId = actor.CreateActorTeam("owner")!;
        var ticket = actor.CreateTicketForTeam(teamId, "visitor", "test").Ticket!;
        var token = actor.Join(teamId, ticket.Token).Member!.MemberToken;
        Assert.True(actor.SendToMemberOnce(teamId, ticket.Name, "unread", "prfactory", "c1").Ok);

        Assert.True(actor.RevokeMember(teamId, ticket.Name));

        using var db = database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM external_messages WHERE recipient<>'lead' AND (read_at IS NULL OR wake_key IS NOT NULL)";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
        Assert.Equal("membership_revoked", actor.Read(token, null, null).Error);
        Assert.True(actor.RevokeMember(teamId, ticket.Name));
    }

    sealed class FakeServer
    {
        public bool LeaseLost { get; set; }
        public PRFactoryWorkItem Item { get; } = new()
        {
            Id = Guid.NewGuid(),
            RepositoryId = Guid.NewGuid(),
            AgentType = PRFactoryAgentType.Codex,
            Prompt = "Review",
            LeaseToken = Guid.NewGuid(),
            TeamPlan = new PRFactoryTeamPlan
            {
                MaxConcurrentChildren = 0,
                Members = [new PRFactoryTeamMember { Name = "visitor", Role = "Reviewer", Order = 1 }]
            }
        };
        public int Claims { get; private set; }
        public List<PRFactoryCommand> Commands { get; } = [];
        public HashSet<Guid> Acknowledged { get; } = [];
        public List<PRFactoryStreamLine> Lines { get; } = [];
        public int StreamPosts { get; private set; }
        public string? CompletionMarkdown { get; private set; }
        public List<string> Calls { get; } = [];

        public HttpResponseMessage Reply(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/poll", StringComparison.Ordinal))
            {
                return Json("{\"workItems\":[" + JsonSerializer.Serialize(Item, PRFactoryWorkItemJson.Default.PRFactoryWorkItem) + "]}");
            }

            if (path.Contains("/claim/", StringComparison.Ordinal))
            {
                Claims++;
                return Json("{\"workItem\":" + JsonSerializer.Serialize(Item, PRFactoryWorkItemJson.Default.PRFactoryWorkItem) + "}");
            }
            if (path.EndsWith("/agent-stream", StringComparison.Ordinal))
            {
                Calls.Add("agent-stream");
                StreamPosts++;
                var batch = JsonSerializer.Deserialize(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(), PRFactoryWorkItemJson.Default.PRFactoryStreamBatch)!;
                if (batch.Lines.Any(line => line.RecordKind == "external-reply"))
                {
                    Calls.Add("external-reply");
                }
                foreach (var line in batch.Lines)
                {
                    if (!Lines.Any(existing => existing.AgentName == line.AgentName && existing.Seq == line.Seq))
                    {
                        Lines.Add(line);
                    }
                }

                var through = batch.Lines.GroupBy(l => l.AgentName).ToDictionary(g => g.Key, g => g.Max(l => l.Seq));
                return Json(JsonSerializer.Serialize(new PRFactoryStreamResponse(true, through, 100, 100000), PRFactoryWorkItemJson.Default.PRFactoryStreamResponse));
            }
            if (path.EndsWith("/agent-commands", StringComparison.Ordinal))
            {
                if (LeaseLost)
                {
                    return new HttpResponseMessage(HttpStatusCode.Conflict);
                }
                return Json(JsonSerializer.Serialize(new PRFactoryCommandDrainResponse([.. Commands.Where(c => !Acknowledged.Contains(c.CommandId))]), PRFactoryWorkItemJson.Default.PRFactoryCommandDrainResponse));
            }

            if (path.EndsWith("/agent-commands/ack", StringComparison.Ordinal))
            {
                var ack = JsonSerializer.Deserialize(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(), PRFactoryWorkItemJson.Default.PRFactoryCommandAckRequest)!;
                foreach (var receipt in ack.Acks)
                {
                    Acknowledged.Add(receipt.CommandId);
                }

                return Json("{\"applied\":1}");
            }
            if (path.Contains("/artefacts/", StringComparison.Ordinal))
            {
                Calls.Add("artefacts");
                return Json("{\"accepted\":true}");
            }
            if (path.Contains("/complete/", StringComparison.Ordinal))
            {
                Calls.Add("complete");
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                CompletionMarkdown = body.RootElement.GetProperty("resultMarkdown").GetString();
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
