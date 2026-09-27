using System.Text.Json;
using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.External;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.External;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class HumanWaitTests
{
    [Fact]
    public void Pruning_retains_active_team_questions_and_retires_terminal_team_questions()
    {
        using var f = new Fixture();
        var row = f.Ask();
        f.Finish();
        f.Waits.PendingNotices(Fixture.Server, f.Team, [(row, "lead:questions")], (_, _, _) => "{}");
        var prune = new PruneJobs(f.State.Database);
        Assert.Empty(prune.Execute(DateTimeOffset.UtcNow.AddDays(1), false));
        f.Teams.Finish(Fixture.Server, f.Team, "completed");
        Assert.Equal([f.Parent], prune.Execute(DateTimeOffset.UtcNow.AddDays(1), false));
        Assert.Null(f.Waits.Get(row.QuestionId));
    }

    [Fact]
    public void Cancelled_answer_turn_becomes_failed_instead_of_waiting_forever()
    {
        using var f = new Fixture();
        var row = f.Ask();
        f.Finish();
        Assert.Null(f.Answer(row, Guid.NewGuid()).Error);
        var resumed = f.Interaction().Advance(row.QuestionId).Wait!;
        Assert.Equal(JobStatus.Cancelled, f.Jobs.Cancel(resumed.ResumedJobId!, "p", "t").Job!.Status);
        var refreshed = f.Waits.Refresh(row.QuestionId).Wait!;
        Assert.Equal("failed", refreshed.Status);
        Assert.Equal("resumed_turn_failed", refreshed.Error);
    }

    [Fact]
    public void Question_then_end_turn_blocks_completion_and_survives_restart()
    {
        using var f = new Fixture();
        var row = f.Ask();
        Assert.Equal("ending_turn", row.Status);
        Assert.True(f.Interaction().BlocksCompletion(Fixture.Server, f.Team));
        Assert.Equal(row.QuestionId, f.Ask().QuestionId);
        Assert.Equal("idempotency_conflict", f.Human.Request(f.Parent, Fixture.Server, f.Team, "different", "question").Error);
        f.Finish();
        var reopened = JobDatabase.Open(f.State.DatabasePath, TimeSpan.FromSeconds(2));
        var store = new HumanWaitStore(reopened);
        Assert.Equal("waiting", store.Refresh(row.QuestionId).Wait!.Status);
        Assert.True(store.BlocksCompletion(Fixture.Server, f.Team));
        Assert.Equal(JobStatus.Completed, f.Jobs.GetJob(f.Parent)!.Status);
    }

    [Fact]
    public void Early_answer_is_held_and_lost_follow_up_reply_recovers_one_turn()
    {
        using var f = new Fixture();
        var row = f.Ask();
        var command = Guid.NewGuid();
        Assert.Null(f.Answer(row, command).Error);
        Assert.Equal("answer_reserved", f.Interaction().Advance(row.QuestionId).Wait!.Status);
        Assert.Single(f.Jobs.ListJobs("p", "t", 20));
        f.Finish();
        var calls = 0;
        var lostReply = f.Interaction(request =>
        {
            var accepted = f.Follow.Execute(request);
            Assert.Null(accepted.Error);
            calls++;
            throw new IOException("lost after durable acceptance");
        });
        Assert.Throws<IOException>(() => lostReply.Advance(row.QuestionId));
        Assert.Equal(1, calls);
        var db = JobDatabase.Open(f.State.DatabasePath, TimeSpan.FromSeconds(2));
        var restarted = new PRFactoryInteraction(new(db), new(db), new(db, DurabilityCheckpoints.None), f.Follow.Execute);
        var resumed = restarted.Advance(row.QuestionId).Wait!;
        Assert.Equal("resumed", resumed.Status);
        Assert.Equal(2, f.Jobs.ListJobs("p", "t", 20).Count);
        Assert.Equal(2, f.Teams.ManagedMembers(Fixture.Server, f.Team).Count);
        Assert.Equal(resumed.ResumedJobId, restarted.Advance(row.QuestionId).Wait!.ResumedJobId);
        Assert.Null(f.Answer(row, command).Error);
        Assert.Equal("idempotency_conflict", f.Answer(row, command, "changed").Error);
        Assert.Equal("question_closed", f.Answer(row, Guid.NewGuid()).Error);
        Assert.True(restarted.BlocksCompletion(Fixture.Server, f.Team));
        var next = f.Jobs.BeginNextAttempt()!;
        var run = new RunRef(next.Job.JobId, next.RunId, next.Generation, next.Correlation);
        f.Jobs.RecordSession(run, "session-one");
        var prompt = HumanWait.AnswerPrompt(resumed);
        Assert.False(restarted.ConfirmManagedInput(row.QuestionId, next.Job.JobId, "wrong-session", Receipt("codex", prompt)));
        Assert.False(restarted.ConfirmManagedInput(row.QuestionId, next.Job.JobId, "session-one", Receipt("codex", prompt, "assistant")));
        Assert.True(restarted.ConfirmManagedInput(row.QuestionId, next.Job.JobId, "session-one", Receipt("codex", prompt)));
        Assert.False(restarted.BlocksCompletion(Fixture.Server, f.Team));
        Assert.Equal(f.Jobs.GetJob(f.Parent)!.Cwd, next.Job.Cwd);
    }

    [Theory]
    [InlineData("cursor")]
    [InlineData("droid")]
    public void Unproven_backends_are_capability_gated(string backend)
    {
        Assert.False(HumanWait.SupportsBackend(backend));
        Assert.False(HumanWaitInputReceipt.Matches(backend, Receipt("codex", "answer"), "answer"));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("pi")]
    public void Native_input_receipts_require_exact_user_input(string backend)
    {
        const string prompt = "[atf-human-answer:question:command]\nanswer";
        Assert.True(HumanWaitInputReceipt.Matches(backend, Receipt(backend, prompt), prompt));
        Assert.False(HumanWaitInputReceipt.Matches(backend, Receipt(backend, prompt, "assistant"), prompt));
        Assert.False(HumanWaitInputReceipt.Matches(backend, Receipt(backend, prompt + "changed"), prompt));
        Assert.False(HumanWaitInputReceipt.Matches(backend, "{\"type\":\"thread.started\"}", prompt));
        Assert.False(HumanWaitInputReceipt.Matches(backend, "[]", prompt));
    }

    [Theory]
    [InlineData("killed")]
    [InlineData("cancelled")]
    [InlineData("fenced")]
    public void Closed_authority_never_resumes_answer(string close)
    {
        using var f = new Fixture();
        var row = f.Ask();
        Assert.Null(f.Answer(row, Guid.NewGuid()).Error);
        f.Finish();
        if (close == "killed") { f.Teams.MarkManagedKilled(Fixture.Server, f.Team, "lead"); }
        else if (close == "cancelled") { f.Teams.Finish(Fixture.Server, f.Team, "failed"); }
        else { f.Teams.SetAcceptance(Fixture.Server, f.Team, "reconciliation_needed"); }
        Assert.Equal("cancelled", f.Interaction().Advance(row.QuestionId).Wait!.Status);
        Assert.Single(f.Jobs.ListJobs("p", "t", 20));
        Assert.True(f.Interaction().BlocksCompletion(Fixture.Server, f.Team));
    }

    [Fact]
    public void Iteration_cap_counts_initial_turn_and_reservations_but_not_retries()
    {
        using var f = new Fixture();
        var row = f.Ask();
        Assert.Equal("max_iterations_exceeded", f.Answer(row, Guid.NewGuid(), max: 1).Error);
        var command = Guid.NewGuid();
        Assert.Null(f.Answer(row, command, max: 2).Error);
        Assert.Null(f.Answer(row, command, max: 2).Error);
        f.Finish();
        var resumed = f.Interaction().Advance(row.QuestionId).Wait!;
        var claim = f.Jobs.BeginNextAttempt()!;
        f.Jobs.RecordSession(new(claim.Job.JobId, claim.RunId, claim.Generation, claim.Correlation), "session-one");
        var second = f.Human.Request(resumed.ResumedJobId!, Fixture.Server, f.Team, "Another?", "next").Wait!;
        Assert.Equal("max_iterations_exceeded", f.Answer(second, Guid.NewGuid(), max: 2).Error);
    }

    [Fact]
    public void Wrong_scope_expired_wait_and_uncertain_turn_are_not_resumable()
    {
        using var f = new Fixture();
        var foreign = new HumanWait(f.Waits, f.Jobs, f.Teams, new("other", "t", "lead"));
        Assert.Equal(JobErrors.NotFound, foreign.Request(f.Parent, Fixture.Server, f.Team, "?", "key").Error);
        var row = f.Human.Request(f.Parent, Fixture.Server, f.Team, "?", "key", DateTimeOffset.UtcNow.AddMinutes(-1)).Wait!;
        Assert.Equal("wait_deadline_exceeded", f.Answer(row, Guid.NewGuid()).Error);
        Assert.Equal("question_not_found", f.Interaction().Answer(Fixture.Server, f.Team, "other", row.QuestionId, Guid.NewGuid(), "yes", 10, DateTimeOffset.UtcNow).Error);
        f.Jobs.QuarantineUncertainAttempts();
        Assert.False(f.Waits.Refresh(row.QuestionId).Wait!.SafeToResume);
        Assert.Single(f.Jobs.ListJobs("p", "t", 20));
    }

    [Fact]
    public void Reserved_answer_cannot_resume_a_superseded_turn()
    {
        using var f = new Fixture();
        var row = f.Ask();
        Assert.Null(f.Answer(row, Guid.NewGuid()).Error);
        f.Finish();
        var newer = f.Follow.Execute(new(f.Parent, "new instruction", "ordinary-command"));
        Assert.Null(newer.Error);
        f.Teams.RecordMember(Fixture.Server, f.Team, "lead", 1, newer.Job!.JobId);
        var result = f.Interaction(_ => throw new InvalidOperationException("stale answer must not resume")).Advance(row.QuestionId);
        Assert.Equal("failed", result.Wait!.Status);
        Assert.Equal("stale_question", result.Wait.Error);
        Assert.False(result.Wait.SafeToResume);
    }

    [Fact]
    public async Task Racing_answers_reserve_only_one_command()
    {
        using var f = new Fixture();
        var row = f.Ask();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => f.Answer(row, Guid.NewGuid()))));
        Assert.Single(results, r => r.Error is null);
        Assert.All(results.Where(r => r.Error is not null), r => Assert.Equal("question_closed", r.Error));
    }

    [Fact]
    public void External_answer_uses_mailbox_once_and_waits_for_read_evidence()
    {
        using var f = new Fixture();
        var external = new ExternalTeam(new ExternalMemberStore(f.State.Database), new WakeStore(f.State.Database));
        var teamId = external.CreateActorTeam("human-test")!;
        var ticket = external.CreateTicketForTeam(teamId, "visitor", null).Ticket!;
        var joined = external.Join(teamId, ticket.Token).Member!;
        f.Teams.RecordExternal(Fixture.Server, f.Team, "visitor", "visitor", teamId, ticket.Token, DateTimeOffset.UtcNow.AddMinutes(10));
        var row = f.Waits.Request(Fixture.Server, f.Team, "visitor", 0, null, "ask", "Which? ").Wait!;
        var command = Guid.NewGuid();
        var interaction = new PRFactoryInteraction(f.Waits, f.Teams, f.Jobs, _ => throw new InvalidOperationException("must not resume an external process"), external);
        Assert.Null(interaction.Answer(Fixture.Server, f.Team, "visitor", row.QuestionId, command, "blue", 1, DateTimeOffset.UtcNow).Error);
        Assert.Null(interaction.Advance(row.QuestionId).Error);
        Assert.Null(interaction.Advance(row.QuestionId).Error);
        Assert.Equal("answer_reserved", f.Waits.Get(row.QuestionId)!.Status);
        var read = external.Read(joined.MemberToken, null, 50);
        Assert.Null(read.Error);
        var inbox = Assert.IsType<ExternalInbox>(read.Inbox);
        Assert.Single(inbox.Messages);
        Assert.True(interaction.ConfirmExternalInput(Fixture.Server, f.Team, "visitor", row.QuestionId, command));
        Assert.Equal("applied", f.Waits.Get(row.QuestionId)!.Status);
    }

    static string Receipt(string backend, string prompt, string role = "user")
    {
        var type = backend switch { "claude" => "user", "pi" => "message", _ => "response_item" };
        var envelope = backend == "codex" ? "payload" : "message";
        return $"{{\"type\":\"{type}\",\"{envelope}\":{{\"role\":\"{role}\",\"content\":[{{\"type\":\"text\",\"text\":\"{JsonEncodedText.Encode(prompt)}\"}}]}}}}";
    }

    sealed class Fixture : IDisposable
    {
        public const string Server = "https://example.test";
        public JobFixture State { get; } = new();
        public JobStore Jobs => State.Store;
        public PRFactoryTeamStore Teams { get; }
        public HumanWaitStore Waits { get; }
        public HumanWait Human { get; }
        public FollowUpJob Follow { get; }
        public Guid Team { get; } = Guid.NewGuid();
        public string Parent { get; }
        readonly RunRef run;

        public Fixture()
        {
            Teams = new(State.Database);
            Waits = new(State.Database);
            Teams.CreateIfAbsent(Server, Team, "{}");
            var principal = new BoundPrincipal("p", "t", "lead");
            var accept = new AcceptJob(Jobs, principal, State.Limits, false, State.Admission, ["codex"]);
            Follow = new(Jobs, principal, accept);
            Parent = accept.Execute(new("initial", "work", null, false) { Backend = "codex" }).Job!.JobId;
            Teams.RecordMember(Server, Team, "lead", 0, Parent);
            var claim = Jobs.BeginNextAttempt()!;
            run = new(Parent, claim.RunId, claim.Generation, claim.Correlation);
            Jobs.RecordSession(run, "session-one");
            Human = new(Waits, Jobs, Teams, principal);
        }

        public HumanWaitRecord Ask() => Human.Request(Parent, Server, Team, "Which color?", "question").Wait!;
        public void Finish() => Assert.True(Jobs.Complete(run, "WaitingForHuman"));
        public PRFactoryInteraction Interaction(Func<FollowUpRequest, JobResult>? follow = null) => new(Waits, Teams, Jobs, follow ?? Follow.Execute);
        public HumanWaitResult Answer(HumanWaitRecord row, Guid command, string answer = "blue", int? max = 10) =>
            Interaction().Answer(Server, Team, row.Member, row.QuestionId, command, answer, max, DateTimeOffset.UtcNow);
        public void Dispose() => State.Dispose();
    }
}
