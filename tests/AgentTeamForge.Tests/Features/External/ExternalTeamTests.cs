using AgentTeamForge.Business.Features.External;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.DAL.Features.External;
using AgentTeamForge.DAL.Features.Sessions;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.Tests.Support;
using AgentTeamForge.Host.Transport;
using System.Text.Json;

namespace AgentTeamForge.Tests.Features.External;

public sealed class ExternalTeamTests
{
    [Fact]
    public void Lead_send_names_unknown_recipient_and_active_members()
    {
        using var f = new JobFixture();
        var lead = new LeadSessionStore(f.Database).Start("/workspace/a", "lead-a");
        var team = Team(f);
        var noMembers = team.SendFromLead(lead.SessionId, lead.Workspace, "team-lead", "report");
        Assert.Equal("member_not_found", noMembers.Error);
        Assert.Contains("team-lead", noMembers.ErrorDetail);
        Assert.Contains("none", noMembers.ErrorDetail);

        var ticket = team.CreateTicket(lead.SessionId, lead.Workspace, "reviewer", null).Ticket!;
        Assert.DoesNotContain("reviewer", team.SendFromLead(lead.SessionId, lead.Workspace, "team-lead", "report").ErrorDetail);
        var token = team.Join(lead.SessionId, ticket.Token).Member!.MemberToken;
        var unknown = team.SendFromLead(lead.SessionId, lead.Workspace, "team-lead", "report");
        Assert.Equal("member_not_found", unknown.Error);
        Assert.Contains("reviewer", unknown.ErrorDetail);
        Assert.True(team.SendFromLead(lead.SessionId, lead.Workspace, "reviewer", "work").Ok);
        Assert.Equal("work", Assert.Single(team.Read(token, null, null).Inbox!.Messages).Text);
        Assert.Equal("invalid_session", team.SendFromLead(null, null, "reviewer", "report").Error);
    }

    [Fact]
    public void Member_token_authenticates_only_a_live_member_name()
    {
        using var f = new JobFixture();
        var lead = new LeadSessionStore(f.Database).Start("/workspace/a", "lead-a");
        var team = Team(f);
        var ticket = team.CreateTicket(lead.SessionId, lead.Workspace, "child-job1", "Managed agent").Ticket!;
        var token = team.Join(ticket.SessionId, ticket.Token).Member!.MemberToken;
        Assert.Equal("child-job1", team.MemberName(token));
        Assert.Null(team.MemberName(token[..^1] + (token[^1] == '0' ? '1' : '0')));
        Assert.Null(team.MemberName(null));
        Assert.True(team.Leave(token).Ok);
        Assert.Null(team.MemberName(token));
    }

    [Fact]
    public void Large_external_pages_fit_ipc_and_only_delivered_rows_advance_the_cursor()
    {
        using var f = new JobFixture();
        var team = Team(f);
        var teamId = team.CreateActorTeam("actor:large-read")!;
        var token = team.Join(teamId, team.CreateTicketForTeam(teamId, "reader", null).Ticket!.Token).Member!.MemberToken;
        var escaped = new string('"', 65529) + "\"\\\n\té😀";
        var unicode = string.Concat(Enumerable.Repeat("😀", 32768));
        Assert.Equal(65536, escaped.Length);
        Assert.Equal(65536, unicode.Length);
        for (var i = 0; i < 50; i++)
        {
            Assert.True(team.SendToMember(teamId, "reader", i % 2 == 0 ? escaped : unicode).Ok);
        }

        var watermark = team.Read(token, null, 0).Inbox!;
        Assert.Empty(watermark.Messages);
        Assert.True(watermark.HasMore);
        Assert.Equal(50, watermark.UnreadCount);
        Assert.Equal(0, watermark.Cursors!["team-lead"]);
        using (var db = f.Database.OpenConnection())
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM external_messages WHERE read_at IS NULL";
            Assert.Equal(50L, command.ExecuteScalar());
        }

        var seen = new List<long>();
        while (seen.Count < 50)
        {
            // Full overrides the count limit, but the transport budget still pages.
            var inbox = team.Read(token, null, 0, full: true).Inbox!;
            Assert.NotEmpty(inbox.Messages);
            foreach (var message in inbox.Messages)
            {
                Assert.Equal(message.Seq % 2 == 1 ? escaped : unicode, message.Text);
            }
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(new IpcResponse(true, Inbox: inbox), IpcJson.Default.IpcResponse).Length
                <= 2 * 1024 * 1024);
            seen.AddRange(inbox.Messages.Select(message => message.Seq));
            Assert.Equal(50 - seen.Count, team.Read(token, null, 0).Inbox!.UnreadCount);
            Assert.Equal(seen.Count < 50, inbox.HasMore);
        }
        Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i), seen);
        Assert.Empty(team.Read(token, null, 50).Inbox!.Messages);
    }

    [Fact]
    public void Lead_read_uses_truncated_text_for_the_page_budget()
    {
        using var f = new JobFixture();
        var lead = new LeadSessionStore(f.Database).Start("/workspace/a", "lead-a");
        var team = Team(f);
        var token = team.Join(lead.SessionId,
            team.CreateTicket(lead.SessionId, lead.Workspace, "writer", null).Ticket!.Token).Member!.MemberToken;
        var text = new string('\\', 65536);
        for (var i = 0; i < 50; i++)
        {
            Assert.True(team.Send(token, text).Ok);
        }

        var watermark = team.ReadLead(lead.SessionId, lead.Workspace, null, 0).Inbox!;
        Assert.Empty(watermark.Messages);
        Assert.Equal(50, watermark.UnreadCount);
        var clipped = team.ReadLead(lead.SessionId, lead.Workspace, null, 50,
            fromAgent: "writer", maxChars: 12).Inbox!;
        Assert.Equal(50, clipped.Messages.Count);
        Assert.False(clipped.HasMore);
        Assert.All(clipped.Messages, message =>
        {
            Assert.Equal(new string('\\', 12), message.Text);
            Assert.True(message.Truncated);
            Assert.Equal(65536, message.FullLen);
        });
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(new IpcResponse(true, Inbox: clipped), IpcJson.Default.IpcResponse).Length
            <= 2 * 1024 * 1024);
        Assert.Empty(team.ReadLead(lead.SessionId, lead.Workspace, null, 0).Inbox!.Messages);
    }

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
    public void Ticket_retry_recovers_membership_until_expiry_and_expired_ticket_fails()
    {
        using var f = new JobFixture();
        var lead = new LeadSessionStore(f.Database).Start("/workspace/a", "lead-a");
        var time = DateTimeOffset.UtcNow;
        var team = Team(f, () => time);
        var ticket = team.CreateTicket(lead.SessionId, lead.Workspace, "member", null).Ticket!;
        Assert.Equal("member-2", team.CreateTicket(lead.SessionId, lead.Workspace, "member", null).Ticket!.Name);
        var joined = team.Join(lead.SessionId, ticket.Token).Member!;
        Assert.StartsWith($"wam1:{lead.SessionId}:", joined.MemberToken);
        var bare = joined.MemberToken.Split(':')[2];
        Assert.True(team.Read(bare, null, 0).Ok);
        Assert.Equal(joined, team.Join(lead.SessionId, ticket.Token).Member);
        // The ticket alone must not derive the member token offline once it expires.
        Assert.NotEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("atf-member:" + ticket.Token))).ToLowerInvariant(), bare);
        Assert.Equal("invalid_or_expired_token", team.Join(lead.SessionId, "bad").Error);
        Assert.Equal("invalid_or_expired_token", team.Join(lead.SessionId, "x" + ticket.Token[1..]).Error);

        var expiring = team.CreateTicket(lead.SessionId, lead.Workspace, "later", null).Ticket!;
        time = time.AddMinutes(11);
        Assert.Equal("invalid_or_expired_token", team.Join(lead.SessionId, expiring.Token).Error);
        Assert.Equal("invalid_or_expired_token", team.Join(lead.SessionId, ticket.Token).Error);
    }

    [Fact]
    public void Member_token_is_scoped_and_leave_revokes_it()
    {
        using var f = new JobFixture();
        var sessions = new LeadSessionStore(f.Database);
        var a = sessions.Start("/workspace/shared", "lead-a");
        var b = sessions.Start("/workspace/shared", "lead-b");
        var team = Team(f);
        var aTicket = team.CreateTicket(a.SessionId, a.Workspace, "member", null).Ticket!;
        var aToken = team.Join(a.SessionId, aTicket.Token).Member!.MemberToken;
        var bToken = team.Join(b.SessionId, team.CreateTicket(b.SessionId, b.Workspace, "member", null).Ticket!.Token).Member!.MemberToken;
        Assert.True(team.SendFromLead(b.SessionId, b.Workspace, "member", "private for b").Ok);
        Assert.Empty(team.Read(aToken, null, null).Inbox!.Messages);
        Assert.Equal("private for b", Assert.Single(team.Read(bToken, null, null).Inbox!.Messages).Text);
        Assert.Equal("invalid_request", team.Read($"wam1:{b.SessionId}:{aToken.Split(':')[2]}", null, null).Error);
        Assert.True(team.Read($"wam1:{a.SessionId}:{aToken.Split(':')[2]}", null, null).Ok);
        Assert.Equal("member", team.Leave(aToken).Name);
        Assert.True(team.Leave(aToken).AlreadyLeft);
        Assert.Equal("membership_revoked", team.Read(aToken, null, null).Error);
        Assert.Equal("membership_revoked", team.Send(aToken, "late").Error);
        Assert.Equal("membership_revoked", team.Join(a.SessionId, aTicket.Token).Error);
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
        Assert.Contains("mcp__agentteamforge__external_read", poster.Notices[0]);
        Assert.Contains("mcp__agentteamforge__read_messages", poster.Notices[0]);

        var first = team.ReadLead(lead.SessionId, lead.Workspace, null, 1).Inbox!;
        Assert.Equal("one", Assert.Single(first.Messages).Text);
        Assert.True(first.HasMore);
        var second = team.ReadLead(lead.SessionId, lead.Workspace, null, 1).Inbox!;
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
        Assert.Equal("invalid_or_expired_token", team.Join(lead.SessionId, unused.Token).Error);
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
    public void Default_read_drains_once_and_sender_cursor_does_not_rewind()
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
        Assert.Empty(team.Read(token, first.NextSeq, null, fromAgent: "team-lead").Inbox!.Messages);

        Assert.True(team.Send(token, "reply").Ok);
        Assert.Single(team.ReadLead(lead.SessionId, lead.Workspace, null, null).Inbox!.Messages);
        Assert.Empty(team.ReadLead(lead.SessionId, lead.Workspace, null, null).Inbox!.Messages);
    }

    [Fact]
    public void Inbox_reads_use_per_sender_cursors_and_support_full_watermark_and_truncation()
    {
        using var f = new JobFixture();
        var lead = new LeadSessionStore(f.Database).Start("/workspace/a", "lead-a");
        var team = Team(f);
        var a = team.Join(lead.SessionId, team.CreateTicket(lead.SessionId, lead.Workspace, "a", null).Ticket!.Token).Member!.MemberToken;
        var b = team.Join(lead.SessionId, team.CreateTicket(lead.SessionId, lead.Workspace, "b", null).Ticket!.Token).Member!.MemberToken;
        Assert.True(team.Send(a, "aaa").Ok);
        Assert.True(team.Send(b, "bbbb").Ok);
        Assert.True(team.Send(a, "ccc").Ok);

        var watermark = team.ReadLead(lead.SessionId, lead.Workspace, null, 0).Inbox!;
        Assert.Empty(watermark.Messages);
        Assert.Equal(3, watermark.UnreadCount);
        Assert.True(watermark.HasMore);
        Assert.Equal(0, watermark.Cursors!["a"]);

        var first = team.ReadLead(lead.SessionId, lead.Workspace, null, 1, maxChars: 2).Inbox!;
        Assert.Equal(("a", 1L, "aa", true, 3),
            (first.Messages[0].From, first.Messages[0].Seq, first.Messages[0].Text,
                first.Messages[0].Truncated, first.Messages[0].FullLen));
        Assert.Equal(3, first.UnreadCount);
        Assert.Equal(1, first.Cursors!["a"]);
        Assert.Equal(0, first.Cursors["b"]);

        var onlyA = team.ReadLead(lead.SessionId, lead.Workspace, 0, 1, fromAgent: "a").Inbox!;
        Assert.Null(onlyA.Cursors);
        Assert.Equal(2, onlyA.SenderSeq);
        Assert.Equal("ccc", Assert.Single(onlyA.Messages).Text);
        Assert.Empty(team.ReadLead(lead.SessionId, lead.Workspace, 0, 0).Inbox!.Messages);

        var rest = team.ReadLead(lead.SessionId, lead.Workspace, null, 1, full: true).Inbox!;
        Assert.Single(rest.Messages);
        Assert.Equal("bbbb", rest.Messages[0].Text);
        Assert.Equal(1, rest.Messages[0].Seq);
        Assert.False(rest.HasMore);
        Assert.Equal(1, rest.Cursors!["b"]);

        Assert.True(team.SendFromLead(lead.SessionId, lead.Workspace, "a", "hello").Ok);
        var memberWatermark = team.Read(a, null, 0).Inbox!;
        Assert.Equal(1, memberWatermark.UnreadCount);
        var memberRead = team.Read(a, 0, 1, fromAgent: "team-lead", maxChars: 2).Inbox!;
        Assert.Equal(1, memberRead.SenderSeq);
        Assert.Equal(("he", true, 5), (memberRead.Messages[0].Text,
            memberRead.Messages[0].Truncated, memberRead.Messages[0].FullLen));
        Assert.Empty(team.Read(a, null, null).Inbox!.Messages);
    }

    [Fact]
    public void Lead_since_seq_follows_inbox_order_across_senders_and_names_invalid_fields()
    {
        using var f = new JobFixture();
        var lead = new LeadSessionStore(f.Database).Start("/workspace/a", "lead-a");
        var team = Team(f);
        var a = team.Join(lead.SessionId, team.CreateTicket(lead.SessionId, lead.Workspace, "a", null).Ticket!.Token).Member!.MemberToken;
        var b = team.Join(lead.SessionId, team.CreateTicket(lead.SessionId, lead.Workspace, "b", null).Ticket!.Token).Member!.MemberToken;

        Assert.True(team.Send(a, "first").Ok);
        var first = team.ReadLead(lead.SessionId, lead.Workspace, null, 1).Inbox!;
        Assert.Equal("first", Assert.Single(first.Messages).Text);
        Assert.True(team.Send(b, "second").Ok);
        var second = team.ReadLead(lead.SessionId, lead.Workspace, first.NextSeq, 1).Inbox!;
        Assert.Equal(("b", 1L, "second"),
            (Assert.Single(second.Messages).From, second.Messages[0].Seq, second.Messages[0].Text));
        Assert.True(second.NextSeq > first.NextSeq);
        Assert.Empty(team.ReadLead(lead.SessionId, lead.Workspace, second.NextSeq, 1).Inbox!.Messages);

        Assert.True(team.Send(a, "third").Ok);
        var filtered = team.ReadLead(lead.SessionId, lead.Workspace, 1, 1, fromAgent: "a").Inbox!;
        Assert.Equal(("third", 2L), (Assert.Single(filtered.Messages).Text, filtered.Messages[0].Seq));

        Assert.Contains("since_seq", team.ReadLead(lead.SessionId, lead.Workspace, -1, 1).ErrorDetail);
        Assert.Contains("limit", team.ReadLead(lead.SessionId, lead.Workspace, null, -1).ErrorDetail);
        Assert.Contains("max_chars", team.ReadLead(lead.SessionId, lead.Workspace, null, 1, maxChars: -1).ErrorDetail);
        Assert.Contains("from_agent", team.ReadLead(lead.SessionId, lead.Workspace, null, 1, fromAgent: "").ErrorDetail);
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
        Assert.True(team.Leave(token).AlreadyLeft);
        Assert.Equal("keep me", Assert.Single(team.Read(stays, null, null).Inbox!.Messages).Text);
        Assert.Equal(1, store.Prune(DateTimeOffset.UtcNow.AddDays(1), dryRun: false));

        var actorWake = wake.Register("codex:actor-leave", "codex", "actor", "", home.Path);
        Assert.True(team.BindTeamWake(teamId, actorWake.Key, actorWake.Generation));
        Assert.True(team.Send(stays, "reply").Ok);
        Assert.Single(wake.PendingExternal());
        Assert.True(team.CloseTeam(teamId));
        Assert.Empty(wake.PendingExternal());
    }

    [Fact]
    public void Prune_read_open_team_messages_preserves_sender_sequence_and_cursor()
    {
        using var f = new JobFixture();
        var time = DateTimeOffset.UtcNow.AddDays(-31);
        var store = new ExternalMemberStore(f.Database);
        var team = new ExternalTeam(store, new WakeStore(f.Database), () => time);
        var teamId = team.CreateActorTeam("actor:prune")!;
        var token = team.Join(teamId, team.CreateTicketForTeam(teamId, "reader", null).Ticket!.Token).Member!.MemberToken;
        Assert.True(team.SendToMember(teamId, "reader", "old read").Ok);
        Assert.True(team.SendToMember(teamId, "reader", "old unread").Ok);
        var first = Assert.Single(team.Read(token, null, 1).Inbox!.Messages);
        Assert.Equal(1, first.Seq);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        Assert.Equal(1, store.Prune(cutoff, dryRun: true));
        Assert.Equal(1, store.Prune(cutoff, dryRun: false));
        var pending = team.Read(token, null, 0).Inbox!;
        Assert.Equal(1, pending.Cursors!["team-lead"]);
        Assert.Equal(1, pending.UnreadCount);
        var second = Assert.Single(team.Read(token, 1, 1, fromAgent: "team-lead").Inbox!.Messages);
        Assert.Equal((2L, "old unread"), (second.Seq, second.Text));
        Assert.Equal(1, store.Prune(cutoff, dryRun: false));

        time = DateTimeOffset.UtcNow;
        Assert.True(team.SendToMember(teamId, "reader", "new").Ok);
        Assert.Equal(2, team.Read(token, null, 0).Inbox!.Cursors!["team-lead"]);
        var third = Assert.Single(team.Read(token, null, 1).Inbox!.Messages);
        Assert.Equal((3L, "new"), (third.Seq, third.Text));
        Assert.Empty(team.Read(token, null, null).Inbox!.Messages);
        Assert.Empty(team.Read(token, 3, null, fromAgent: "team-lead").Inbox!.Messages);
    }

    [Fact]
    public void Prune_keeps_connector_team_cursor_and_unread_replies()
    {
        using var f = new JobFixture();
        var time = DateTimeOffset.UtcNow.AddDays(-31);
        var store = new ExternalMemberStore(f.Database);
        var team = new ExternalTeam(store, new WakeStore(f.Database), () => time);
        var teamId = team.CreateActorTeam("actor:prune-connector")!;
        var token = team.Join(teamId, team.CreateTicketForTeam(teamId, "worker", null).Ticket!.Token).Member!.MemberToken;
        Assert.True(team.Send(token, "first").Ok);
        Assert.True(team.Send(token, "second").Ok);
        var cursor = team.ReadTeam(teamId, 0, 1).Inbox!.NextSeq;

        Assert.Equal(1, store.Prune(DateTimeOffset.UtcNow.AddDays(-30), dryRun: false));
        Assert.Equal("second", Assert.Single(team.ReadTeam(teamId, cursor, 50).Inbox!.Messages).Text);
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
