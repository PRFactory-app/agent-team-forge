using System.Net;
using System.Text;
using System.Text.Json;
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
        Assert.Contains("join_team", Assert.Single(server.Lines).Text);
        Assert.Contains("join_team", server.Lines[0].Text);
        var state = StateDirectory.Open(dir.Path);
        PRFactoryConnection.PublishJoinTickets(state, store, "https://example.test");
        Assert.Contains("join_team", Encoding.UTF8.GetString(StateDirectory.ReadPrivateFile(dir.File("prfactory-joins.json"))));
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
        Assert.Single(actor.Read(token, 0, null).Inbox!.Messages);

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

    sealed class FakeServer
    {
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
                StreamPosts++;
                var batch = JsonSerializer.Deserialize(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(), PRFactoryWorkItemJson.Default.PRFactoryStreamBatch)!;
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
