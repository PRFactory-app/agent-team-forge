using System.IO.Pipes;
using AgentTeamForge.Business.Features.External;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.DAL.Features.External;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Features.Wake;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Wake;

public sealed class ClaudeMemberWakeTests
{
    [Theory]
    [InlineData("linux", "unix")]
    [InlineData("macos", "unix")]
    [InlineData("windows", "pipe")]
    [InlineData("unknown", null)]
    public void Selects_native_transport(string platform, string? expected) =>
        Assert.Equal(expected, ClaudeChannel.Transport(platform));

    [Theory]
    [InlineData(@"\\.\pipe\claude-channel", "claude-channel")]
    [InlineData(@"\\.\PIPE\claude-channel", "claude-channel")]
    [InlineData(@"\\remote\pipe\claude-channel", null)]
    [InlineData(@"\\.\pipe\", null)]
    [InlineData(@"\\.\pipe\nested\name", null)]
    [InlineData("/tmp/123.sock", null)]
    public void Only_local_flat_pipe_names_are_accepted(string path, string? name) =>
        Assert.Equal(name, ClaudeChannel.PipeName(path));

    [Fact]
    public void Windows_requires_same_user_overlapped_io_and_explicit_host_identity()
    {
        Assert.True(ClaudePipe.Access.HasFlag(PipeAccessRights.ReadPermissions));
        Assert.True(ClaudePipe.SameUser("S-1-5-21-1", "S-1-5-21-1"));
        Assert.False(ClaudePipe.SameUser("S-1-5-21-1", "S-1-5-32-544"));
        Assert.False(ClaudePipe.SameUser(null, null));
        Assert.True(ClaudePipe.Options.HasFlag(PipeOptions.Asynchronous));
        Assert.True(ClaudeChannel.Valid(@"\\.\pipe\claude", "token", "123", "windows"));
        Assert.False(ClaudeChannel.Valid(@"\\.\pipe\claude", "token", "", "windows"));
        Assert.False(ClaudeChannel.Valid(@"\\server\pipe\claude", "token", "123", "windows"));
    }

    [Fact]
    public void Host_binding_rejects_inherited_channels_and_model_supplied_credentials()
    {
        using var dir = new TempStateDir();
        var socket = dir.File("123.sock");
        File.WriteAllText(socket, "");
        Assert.Null(HostSessionWake.ForClaudeChannel((123, "codex"), socket, "secret", "linux"));
        Assert.Null(HostSessionWake.ForClaudeChannel((124, "claude"), socket, "secret", "macos"));
        var host = HostSessionWake.ForClaudeChannel((123, "claude"), socket, "secret", "macos");
        Assert.NotNull(host);
        var supplied = new IpcRequest { Op = IpcProtocol.ExternalSetWake, WakeAddress = "untrusted", WakeSecret = "untrusted" };
        var bound = JobsMcpBridge.ClaudeMemberWake(supplied, host);
        Assert.Equal(socket, bound.WakeAddress);
        Assert.Equal("secret", bound.WakeSecret);
        Assert.Null(JobsMcpBridge.ClaudeMemberWake(supplied, null).WakeSecret);
        Assert.Equal((123, "claude"), MacHostAncestry.Resolve(456, "456 123 /tmp/atf\n123 1 /opt/bin/claude\n"));
        Assert.Equal((456, "codex"), MacHostAncestry.Resolve(456, "456 123 /tmp/codex\n123 1 /opt/bin/claude\n"));
    }

    [Fact]
    public async Task Claude_member_registration_wakes_only_committed_messages_without_consuming_them()
    {
        using var f = new JobFixture();
        var wake = new WakeStore(f.Database);
        var team = new ExternalTeam(new ExternalMemberStore(f.Database), wake);
        var id = team.CreateActorTeam("actor:claude-test")!;
        var token = team.Join(id, team.CreateTicketForTeam(id, "claude-reader", null).Ticket!.Token).Member!.MemberToken;
        var address = OperatingSystem.IsWindows() ? @"\\.\pipe\claude-test" : "/tmp/123.sock";
        Assert.Equal("invalid_claude_wake", team.SetClaudeWake(token, address, "", "123").Error);
        Assert.Equal("invalid_claude_wake", team.SetClaudeWake(token, address, "secret", "0").Error);
        Assert.NotNull(team.SetClaudeWake(token, address, "secret", "123").WakeGeneration);
        Assert.Empty(wake.PendingExternal());
        var poster = new CheckPoster(() =>
        {
            // A separately opened connection must already see the committed row during posting.
            using var db = f.Database.OpenConnection();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT text FROM external_messages WHERE read_at IS NULL";
            Assert.Equal("private task body", cmd.ExecuteScalar());
        });
        var coordinator = new WakeCoordinator(wake, poster, _ => { }, coalesce: TimeSpan.Zero);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, poster.Count);
        Assert.True(team.SendToMember(id, "claude-reader", "private task body").Ok);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, poster.Count);
        Assert.Equal(1, Assert.Single(wake.PendingExternal()).Unread);
        Assert.Equal("private task body", Assert.Single(team.Read(token, null, null).Inbox!.Messages).Text);
        Assert.Empty(wake.PendingExternal());
        Assert.True(team.SetWake(token, "", null).Ok);
        Assert.True(team.SendToMember(id, "claude-reader", "after clear").Ok);
        Assert.Empty(wake.PendingExternal());
        Assert.NotNull(team.SetClaudeWake(token, address, "secret", "123").WakeGeneration);
        Assert.Single(wake.PendingExternal());
        Assert.True(team.Leave(token).Ok);
        Assert.Empty(wake.PendingExternal());
        Assert.Equal("membership_revoked", team.SetClaudeWake(token, address, "secret", "123").Error);
    }

    [Fact]
    public async Task Host_local_mailbox_requires_channel_identity_and_receipt_and_retries_after_failure()
    {
        var mailbox = new ClaudeWakeMailbox();
        var target = new WakeRegistration("external:test", 1, "claude", "/tmp/123.sock", "secret", "123");
        var pending = mailbox.PostAsync(target, "notice only", TestContext.Current.CancellationToken);
        Assert.Null(mailbox.Take(target.Address, "wrong", target.Home));
        Assert.Null(mailbox.Take(target.Address, target.Secret, "456"));
        var offer = Assert.IsType<ClaudeWakeNotice>(mailbox.Take(target.Address, target.Secret, target.Home));
        Assert.False(pending.IsCompleted);
        Assert.Null(mailbox.Take(target.Address, target.Secret, target.Home));
        Assert.False(mailbox.Complete(offer.Id, target.Address, "wrong", target.Home, true));
        Assert.True(mailbox.Complete(offer.Id, target.Address, target.Secret, target.Home, false));
        Assert.False(await pending);
        var retry = mailbox.PostAsync(target, "notice only", TestContext.Current.CancellationToken);
        var next = Assert.IsType<ClaudeWakeNotice>(mailbox.Take(target.Address, target.Secret, target.Home));
        Assert.NotEqual(offer.Id, next.Id);
        Assert.True(mailbox.Complete(next.Id, target.Address, target.Secret, target.Home, true));
        Assert.True(await retry);
        using var cancellation = new CancellationTokenSource();
        var abandoned = mailbox.PostAsync(target, "notice only", cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.Null(mailbox.Take(target.Address, target.Secret, target.Home));
    }

    sealed class CheckPoster(Action check) : IWakePoster
    {
        public int Count;
        public Task<bool> PostAsync(WakeRegistration target, string notice, CancellationToken cancellationToken)
        {
            check();
            Assert.Equal("claude", target.Kind);
            Assert.DoesNotContain("private task body", notice);
            Assert.DoesNotContain("secret", notice);
            Count++;
            return Task.FromResult(true);
        }
    }
}
