using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.Tests.Support;
using System.Net.Sockets;
using System.Text;

namespace AgentTeamForge.Tests.Features.Wake;

public sealed class WakeTests
{
    sealed class FakePoster(Func<WakeRegistration, string, bool>? send = null) : IWakePoster
    {
        public readonly List<(WakeRegistration Target, string Notice)> Attempts = [];
        public Task<bool> PostAsync(WakeRegistration target, string notice, CancellationToken cancellationToken)
        {
            Attempts.Add((target, notice));
            return Task.FromResult(send?.Invoke(target, notice) ?? true);
        }
    }

    static string Finish(JobFixture fixture, WakeRegistration target, string key)
    {
        var accepted = fixture.Accept().Execute(new SubmitJobRequest(key, "secret result", null, false, target.Key, target.Generation));
        Assert.Equal("accepted", accepted.Outcome);
        var claim = fixture.Store.BeginNextAttempt()!;
        Assert.True(fixture.Store.Complete(new(claim.Job.JobId, claim.RunId, claim.Generation, claim.Correlation), "secret result"));
        return claim.Job.JobId;
    }

    [Fact]
    public async Task Coalesces_committed_jobs_and_only_reposts_after_read_or_renotify()
    {
        using var fixture = new JobFixture();
        var store = new WakeStore(fixture.Database);
        var target = store.Register("codex:test", "codex", "thread", "", "/tmp");
        var first = Finish(fixture, target, "one");
        var second = Finish(fixture, target, "two");
        var time = DateTimeOffset.UtcNow;
        var poster = new FakePoster((_, notice) =>
        {
            Assert.DoesNotContain("secret result", notice);
            Assert.Equal("completed", fixture.Store.GetJob(first)!.Status);
            Assert.Equal("completed", fixture.Store.GetJob(second)!.Status);
            return true;
        });
        var coordinator = new WakeCoordinator(store, poster, _ => { }, () => time);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        time += TimeSpan.FromSeconds(1);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        Assert.Empty(poster.Attempts);
        time += TimeSpan.FromSeconds(1);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        Assert.Single(poster.Attempts);
        Assert.Contains("2 completed job(s)", poster.Attempts[0].Notice);
        time += TimeSpan.FromSeconds(10);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        Assert.Single(poster.Attempts);
        store.MarkRead(first, target.Key, target.Generation);
        store.MarkRead(second, target.Key, target.Generation);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        Finish(fixture, target, "three");
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        time += TimeSpan.FromSeconds(2);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, poster.Attempts.Count);
    }

    [Fact]
    public async Task Failed_post_retries_with_bounded_backoff_and_keeps_unread()
    {
        using var fixture = new JobFixture();
        var store = new WakeStore(fixture.Database);
        var target = store.Register("codex:test", "codex", "thread", "", "/tmp");
        Finish(fixture, target, "one");
        var time = DateTimeOffset.UtcNow;
        var poster = new FakePoster((_, _) => false);
        var coordinator = new WakeCoordinator(store, poster, _ => { }, () => time, TimeSpan.Zero);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        Assert.Single(poster.Attempts);
        time += TimeSpan.FromSeconds(1);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        Assert.Single(poster.Attempts);
        time += TimeSpan.FromSeconds(1);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, poster.Attempts.Count);
        Assert.Equal(1, store.Pending().Single().Unread);
        var backoff = new WakeBackoff();
        for (var i = 0; i < 20; i++)
        {
            backoff.Failed(time);
        }

        Assert.Equal(TimeSpan.FromMinutes(5), backoff.Delay);
    }

    [Fact]
    public async Task Re_registration_replaces_generation_and_catches_up_unread()
    {
        using var fixture = new JobFixture();
        var store = new WakeStore(fixture.Database);
        var old = store.Register("codex:test", "codex", "old", "", "/tmp");
        var jobId = Finish(fixture, old, "one");
        var current = store.Register(old.Key, "codex", "new", "", "/tmp");
        Assert.Equal(old.Generation + 1, current.Generation);
        Assert.False(store.MarkNotified(new(old, 1, 1, 0, null, false), DateTimeOffset.UtcNow));
        var time = DateTimeOffset.UtcNow;
        var poster = new FakePoster();
        var coordinator = new WakeCoordinator(store, poster, _ => { }, () => time);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        time += TimeSpan.FromSeconds(2);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(current, Assert.Single(poster.Attempts).Target);
        store.MarkRead(jobId, old.Key, old.Generation);
        Assert.Equal(1, store.Pending().Single().Unread);
        store.MarkRead(jobId, current.Key, current.Generation);
        Assert.Empty(store.Pending());
    }

    [Fact]
    public async Task Codex_adapter_checks_thread_before_queuing()
    {
        var verified = false;
        var queued = false;
        var target = new WakeRegistration("codex:test", 1, "codex", "thread", "", "/tmp");
        var adapter = new CodexQueueWake(_ => verified, (_, _, _) => { queued = true; return Task.FromResult(true); });
        Assert.False(await adapter.PostAsync(target, "notice", CancellationToken.None));
        Assert.False(queued);
        verified = true;
        Assert.True(await adapter.PostAsync(target, "notice", CancellationToken.None));
        Assert.True(queued);
    }

    [Fact]
    public async Task Claude_adapter_posts_two_notice_only_json_lines_to_fake_socket()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var dir = new TempStateDir();
        var path = dir.File("claude.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var accepted = listener.AcceptAsync(TestContext.Current.CancellationToken);
        var target = new WakeRegistration("claude:test", 1, "claude", path, "token", "");
        var poster = new ClaudeChannelWake();
        Assert.True(await poster.PostAsync(target, "call job_get", TestContext.Current.CancellationToken));
        using var client = await accepted;
        using var stream = new NetworkStream(client, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal("{\"type\":\"auth\",\"token\":\"token\"}", await reader.ReadLineAsync(TestContext.Current.CancellationToken));
        Assert.Contains("call job_get", await reader.ReadLineAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pi_adapter_writes_private_notice_for_extension()
    {
        using var dir = new TempStateDir();
        var path = dir.File("pi-wake-123.jsonl");
        var target = new WakeRegistration("pi:123", 2, "pi", path, "", "");
        var poster = new PiExtensionWake(dir.Path);
        Assert.True(await poster.PostAsync(target, "call job_get", TestContext.Current.CancellationToken));
        Assert.Contains("call job_get", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }
}
