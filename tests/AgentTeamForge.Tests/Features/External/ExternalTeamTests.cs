using AgentTeamForge.Business.Features.External;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.DAL.Features.External;
using AgentTeamForge.DAL.Features.Sessions;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.External;

public sealed class ExternalTeamTests
{
    [Fact]
    public void In_daemon_actor_can_own_team_without_mcp_lead_session()
    {
        using var f = new JobFixture();
        using var home = new TempStateDir();
        var sessionsDir = home.File("sessions");
        Directory.CreateDirectory(sessionsDir);
        var thread = Guid.NewGuid().ToString("D");
        File.WriteAllText(Path.Combine(sessionsDir, "rollout-test-" + thread + ".jsonl"), "");
        var wake = new WakeStore(f.Database);
        var team = new ExternalTeam(new ExternalMemberStore(f.Database), wake);
        var teamId = team.CreateActorTeam("prfactory:test:workitem-1")!;
        Assert.Equal(teamId, team.CreateActorTeam("prfactory:test:workitem-1"));
        var ticket = team.CreateTicketForTeam(teamId, "visitor", null).Ticket!;
        var memberToken = team.Join(teamId, ticket.Token).Member!.MemberToken;
        Assert.NotNull(team.SetWake(memberToken, thread, home.Path).WakeGeneration);
        Assert.True(team.SendToMember(teamId, "visitor", "server prompt", "prfactory").Ok);
        Assert.Single(wake.PendingExternal());
        Assert.Equal("server prompt", Assert.Single(team.Read(memberToken, null, null).Inbox!.Messages).Text);
        var actorWake = wake.Register("codex:actor-test", "codex", "actor", "", home.Path);
        Assert.True(team.BindTeamWake(teamId, actorWake.Key, actorWake.Generation));
        Assert.True(team.Send(memberToken, "agent reply").Ok);
        Assert.Equal(actorWake.Key, Assert.Single(wake.PendingExternal()).Target.Key);
        var reply = Assert.Single(team.ReadTeam(teamId, null, null).Inbox!.Messages);
        Assert.Equal(("visitor", "agent reply"), (reply.From, reply.Text));
        Assert.True(team.CloseTeam(teamId));
        Assert.Equal("membership_revoked", team.Read(memberToken, null, null).Error);
        using var db = f.Database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM lead_sessions";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void Ticket_is_single_use_and_expires()
    {
        using var f = new JobFixture();
        var lead = new LeadSessionStore(f.Database).Start("/workspace/a", "lead-a");
        var time = DateTimeOffset.UtcNow;
        var team = Team(f, () => time);
        var ticket = team.CreateTicket(lead.SessionId, lead.Workspace, "member", null).Ticket!;
        Assert.Equal("member-2", team.CreateTicket(lead.SessionId, lead.Workspace, "member", null).Ticket!.Name);
        Assert.NotNull(team.Join(lead.SessionId, ticket.Token).Member);
        Assert.Equal("invalid_or_expired_ticket", team.Join(lead.SessionId, ticket.Token).Error);

        var expiring = team.CreateTicket(lead.SessionId, lead.Workspace, "later", null).Ticket!;
        time = time.AddMinutes(11);
        Assert.Equal("invalid_or_expired_ticket", team.Join(lead.SessionId, expiring.Token).Error);
    }

    [Fact]
    public void Member_token_is_scoped_and_leave_revokes_it()
    {
        using var f = new JobFixture();
        var sessions = new LeadSessionStore(f.Database);
        var a = sessions.Start("/workspace/shared", "lead-a");
        var b = sessions.Start("/workspace/shared", "lead-b");
        var team = Team(f);
        var aToken = team.Join(a.SessionId, team.CreateTicket(a.SessionId, a.Workspace, "member", null).Ticket!.Token).Member!.MemberToken;
        var bToken = team.Join(b.SessionId, team.CreateTicket(b.SessionId, b.Workspace, "member", null).Ticket!.Token).Member!.MemberToken;
        Assert.True(team.SendFromLead(b.SessionId, b.Workspace, "member", "private for b").Ok);
        Assert.Empty(team.Read(aToken, null, null).Inbox!.Messages);
        Assert.Equal("private for b", Assert.Single(team.Read(bToken, null, null).Inbox!.Messages).Text);
        Assert.True(team.Leave(aToken).Ok);
        Assert.Equal("membership_revoked", team.Read(aToken, null, null).Error);
        Assert.Equal("membership_revoked", team.Send(aToken, "late").Error);
    }

    [Fact]
    public async Task Send_and_read_round_trip_with_cursor_and_lead_wake_after_commit()
    {
        using var f = new JobFixture();
        var sessions = new LeadSessionStore(f.Database);
        var lead = sessions.Start("/workspace/a", "lead-a");
        var wake = new WakeStore(f.Database);
        var target = wake.Register("codex:test-lead", "codex", "test", "", "/tmp/test-home");
        sessions.BindWake(lead.SessionId, target.Key, target.Generation);
        var team = new ExternalTeam(new ExternalMemberStore(f.Database), wake);
        var token = team.Join(lead.SessionId, team.CreateTicket(lead.SessionId, lead.Workspace, "member", null).Ticket!.Token).Member!.MemberToken;
        Assert.True(team.Send(token, "one").Ok);
        Assert.True(team.Send(token, "two").Ok);
        var snapshot = Assert.Single(wake.PendingExternal());
        Assert.Equal(2, snapshot.Unread);
        var poster = new RecordingPoster();
        await new WakeCoordinator(wake, poster, _ => { }, coalesce: TimeSpan.Zero).TickAsync(TestContext.Current.CancellationToken);
        Assert.Single(poster.Notices);
        Assert.Contains("external message", poster.Notices[0]);

        var first = team.ReadLead(lead.SessionId, lead.Workspace, null, 1).Inbox!;
        Assert.Equal("one", Assert.Single(first.Messages).Text);
        Assert.True(first.HasMore);
        var second = team.ReadLead(lead.SessionId, lead.Workspace, first.NextSeq, 1).Inbox!;
        Assert.Equal("two", Assert.Single(second.Messages).Text);
        Assert.False(second.HasMore);
        Assert.Empty(wake.PendingExternal());
        Assert.True(team.SendFromLead(lead.SessionId, lead.Workspace, "member", "reply").Ok);
        var watermark = team.Read(token, null, 0).Inbox!;
        Assert.Empty(watermark.Messages);
        Assert.True(watermark.HasMore);
        var truncated = Assert.Single(team.Read(token, null, 1, fromAgent: "team-lead", maxChars: 3).Inbox!.Messages);
        Assert.Equal(("rep", true, 5), (truncated.Text, truncated.Truncated, truncated.FullLen));
    }

    [Fact]
    public void Closing_lead_revokes_member_and_ticket()
    {
        using var f = new JobFixture();
        var sessions = new LeadSessionStore(f.Database);
        var lead = sessions.Start("/workspace/a", "lead-a");
        var team = Team(f);
        var token = team.Join(lead.SessionId, team.CreateTicket(lead.SessionId, lead.Workspace, "joined", null).Ticket!.Token).Member!.MemberToken;
        var unused = team.CreateTicket(lead.SessionId, lead.Workspace, "unused", null).Ticket!;
        Assert.True(sessions.Close(lead.SessionId, lead.Workspace));
        Assert.Equal("membership_revoked", team.Read(token, null, null).Error);
        Assert.Equal("invalid_or_expired_ticket", team.Join(lead.SessionId, unused.Token).Error);
    }

    [Fact]
    public void Member_can_opt_into_and_clear_codex_queue_wake_using_disposable_home()
    {
        using var f = new JobFixture();
        using var home = new TempStateDir();
        var sessionsDir = home.File("sessions");
        Directory.CreateDirectory(sessionsDir);
        var thread = Guid.NewGuid().ToString("D");
        File.WriteAllText(Path.Combine(sessionsDir, "rollout-test-" + thread + ".jsonl"), "");
        var sessions = new LeadSessionStore(f.Database);
        var lead = sessions.Start("/workspace/a", "lead-a");
        var wake = new WakeStore(f.Database);
        var team = new ExternalTeam(new ExternalMemberStore(f.Database), wake);
        var token = team.Join(lead.SessionId, team.CreateTicket(lead.SessionId, lead.Workspace, "member", null).Ticket!.Token).Member!.MemberToken;
        Assert.NotNull(team.SetWake(token, thread, home.Path).WakeGeneration);
        Assert.True(team.SendFromLead(lead.SessionId, lead.Workspace, "member", "notice").Ok);
        Assert.StartsWith("external:", Assert.Single(wake.PendingExternal()).Target.Key);
        Assert.True(team.SetWake(token, "", null).Ok);
        Assert.Empty(wake.PendingExternal());
        Assert.Equal("notice", Assert.Single(team.Read(token, null, null).Inbox!.Messages).Text);
    }

    [Fact]
    public void Default_read_drains_once_and_explicit_cursor_rereads()
    {
        using var f = new JobFixture();
        var lead = new LeadSessionStore(f.Database).Start("/workspace/a", "lead-a");
        var team = Team(f);
        var token = team.Join(lead.SessionId, team.CreateTicket(lead.SessionId, lead.Workspace, "member", null).Ticket!.Token).Member!.MemberToken;
        Assert.True(team.SendFromLead(lead.SessionId, lead.Workspace, "member", "one").Ok);
        Assert.True(team.SendFromLead(lead.SessionId, lead.Workspace, "member", "two").Ok);
        var first = team.Read(token, null, 1).Inbox!;
        Assert.Equal("one", Assert.Single(first.Messages).Text);
        Assert.Equal("two", Assert.Single(team.Read(token, null, null).Inbox!.Messages).Text);
        Assert.Empty(team.Read(token, null, null).Inbox!.Messages);
        Assert.Equal("two", Assert.Single(team.Read(token, first.NextSeq, null).Inbox!.Messages).Text);

        Assert.True(team.Send(token, "reply").Ok);
        Assert.Single(team.ReadLead(lead.SessionId, lead.Workspace, null, null).Inbox!.Messages);
        Assert.Empty(team.ReadLead(lead.SessionId, lead.Workspace, null, null).Inbox!.Messages);
    }

    [Fact]
    public void Leave_and_close_stop_wake_and_prune_keeps_unread_open_team_mail()
    {
        using var f = new JobFixture();
        using var home = new TempStateDir();
        Directory.CreateDirectory(home.File("sessions"));
        var thread = Guid.NewGuid().ToString("D");
        File.WriteAllText(Path.Combine(home.File("sessions"), "rollout-test-" + thread + ".jsonl"), "");
        var store = new ExternalMemberStore(f.Database);
        var wake = new WakeStore(f.Database);
        var team = new ExternalTeam(store, wake);
        var teamId = team.CreateActorTeam("actor:leave")!;
        var token = team.Join(teamId, team.CreateTicketForTeam(teamId, "gone", null).Ticket!.Token).Member!.MemberToken;
        var stays = team.Join(teamId, team.CreateTicketForTeam(teamId, "stays", null).Ticket!.Token).Member!.MemberToken;
        Assert.NotNull(team.SetWake(token, thread, home.Path).WakeGeneration);
        Assert.True(team.SendToMember(teamId, "gone", "unread").Ok);
        Assert.True(team.SendToMember(teamId, "stays", "keep me").Ok);
        Assert.Single(wake.PendingExternal());
        Assert.True(team.Leave(token).Ok);
        Assert.Empty(wake.PendingExternal());

        Assert.Equal(1, store.Prune(DateTimeOffset.UtcNow.AddDays(1), dryRun: false));
        Assert.Equal("keep me", Assert.Single(team.Read(stays, null, null).Inbox!.Messages).Text);
        Assert.Equal(1, store.Prune(DateTimeOffset.UtcNow.AddDays(1), dryRun: false));

        var actorWake = wake.Register("codex:actor-leave", "codex", "actor", "", home.Path);
        Assert.True(team.BindTeamWake(teamId, actorWake.Key, actorWake.Generation));
        Assert.True(team.Send(stays, "reply").Ok);
        Assert.Single(wake.PendingExternal());
        Assert.True(team.CloseTeam(teamId));
        Assert.Empty(wake.PendingExternal());
    }

    static ExternalTeam Team(JobFixture f, Func<DateTimeOffset>? clock = null) =>
        new(new ExternalMemberStore(f.Database), new WakeStore(f.Database), clock);

    sealed class RecordingPoster : IWakePoster
    {
        public List<string> Notices { get; } = [];
        public Task<bool> PostAsync(WakeRegistration target, string notice, CancellationToken cancellationToken)
        {
            Notices.Add(notice);
            return Task.FromResult(true);
        }
    }
}
