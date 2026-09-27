using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.DAL.Features.Sessions;
using AgentTeamForge.Host.Features.Wake;
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

    sealed class DelayedPoster : IWakePoster
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<bool> PostAsync(WakeRegistration target, string notice, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return true;
        }
    }

    static string Finish(JobFixture fixture, WakeRegistration target, string key)
    {
        var accepted = fixture.Accept().Execute(new SubmitJobRequest(key, "secret result", null, false) { WakeKey = target.Key, WakeGeneration = target.Generation });
        Assert.Equal("accepted", accepted.Outcome);
        var claim = fixture.Store.BeginNextAttempt()!;
        Assert.True(fixture.Store.Complete(new(claim.Job.JobId, claim.RunId, claim.Generation, claim.Correlation), "secret result"));
        return claim.Job.JobId;
    }

    [Fact]
    public void Cancelled_job_counts_as_finished_and_can_be_marked_read()
    {
        using var fixture = new JobFixture();
        var store = new WakeStore(fixture.Database);
        var target = store.Register("codex:test", "codex", "thread", "", "/tmp");
        var job = fixture.Accept().Execute(new SubmitJobRequest("stopped", "work", null, false) { WakeKey = target.Key, WakeGeneration = target.Generation }).Job!;
        Assert.Empty(store.Pending());

        new StopJob(fixture.Store, JobFixture.Operator, _ => { }).Execute(job.JobId);

        Assert.Equal(1, Assert.Single(store.Pending()).Unread);
        store.MarkRead(job.JobId, target.Key, target.Generation);
        Assert.Empty(store.Pending());
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
        Assert.Contains("list_jobs", poster.Attempts[0].Notice);
        Assert.Contains("get_job", poster.Attempts[0].Notice);
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
    public async Task Clear_waits_for_delayed_post_then_tombstones_and_rebinds_unread_after_restart()
    {
        using var fixture = new JobFixture();
        var wake = new WakeStore(fixture.Database);
        var sessions = new LeadSessionStore(fixture.Database);
        var lead = sessions.Start("/workspace/wake", "parent=1");
        var first = wake.Register("codex:first", "codex", "first", "", "/tmp");
        sessions.BindWake(lead.SessionId, first.Key, first.Generation);
        var accepted = fixture.Accept().Execute(new SubmitJobRequest("one", "secret result", null, false)
        { LeadSessionId = lead.SessionId, WakeKey = first.Key, WakeGeneration = first.Generation });
        var claim = fixture.Store.BeginNextAttempt()!;
        fixture.Store.Complete(new(claim.Job.JobId, claim.RunId, claim.Generation, claim.Correlation), "done");
        var poster = new DelayedPoster();
        var coordinator = new WakeCoordinator(wake, poster, _ => { }, coalesce: TimeSpan.Zero);
        var posting = coordinator.TickAsync(TestContext.Current.CancellationToken);
        await poster.Started.Task;
        var clearing = Task.Run(() =>
        {
            using var gate = WakeRoutingGate.Enter();
            return wake.ClearLead(lead.SessionId, first.Key, first.Generation);
        });
        Assert.False(clearing.IsCompleted);
        poster.Release.SetResult();
        await posting;
        Assert.True(await clearing);
        Assert.False(wake.IsCurrent(first));
        Assert.Empty(wake.Pending());
        var reopened = AgentTeamForge.DAL.Sqlite.JobDatabase.Open(fixture.Database.Path, TimeSpan.FromSeconds(2));
        var restartedWake = new WakeStore(reopened);
        var restartedSessions = new LeadSessionStore(reopened);
        var second = restartedWake.Register("codex:second", "codex", "second", "", "/tmp");
        restartedSessions.BindWake(lead.SessionId, second.Key, second.Generation);
        Assert.Equal(second.Key, Assert.Single(restartedWake.Pending()).Target.Key);
        Assert.Equal(accepted.Job!.JobId, claim.Job.JobId);
    }

    [Fact]
    public void Codex_queue_requires_submission_id_even_on_successful_exit()
    {
        Assert.False(CodexQueueWake.HasSubmissionId(""));
        Assert.False(CodexQueueWake.HasSubmissionId("{\"status\":\"ok\"}"));
        Assert.True(CodexQueueWake.HasSubmissionId("{\"submission_id\":\"queued-123\"}"));
    }

    [Fact]
    public async Task Replacement_during_delayed_post_queues_only_on_the_new_target_after_return()
    {
        using var fixture = new JobFixture();
        var wake = new WakeStore(fixture.Database);
        var old = wake.Register("codex:old", "codex", "old", "", "/tmp");
        Finish(fixture, old, "one");
        var delayed = new DelayedPoster();
        var posting = new WakeCoordinator(wake, delayed, _ => { }, coalesce: TimeSpan.Zero)
            .TickAsync(TestContext.Current.CancellationToken);
        await delayed.Started.Task;
        var replacing = Task.Run(() =>
        {
            using var gate = WakeRoutingGate.Enter();
            return wake.Register(old.Key, "codex", "new", "", "/tmp");
        });
        Assert.False(replacing.IsCompleted);
        delayed.Release.SetResult();
        await posting;
        var current = await replacing;
        Assert.False(wake.IsCurrent(old));
        var notices = new FakePoster();
        await new WakeCoordinator(wake, notices, _ => { }, coalesce: TimeSpan.Zero)
            .TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(current, Assert.Single(notices.Attempts).Target);
    }

    [Fact]
    public async Task Run_stops_quietly_when_cancelled_during_a_post()
    {
        using var fixture = new JobFixture();
        var store = new WakeStore(fixture.Database);
        var target = store.Register("codex:test", "codex", "thread", "", "/tmp");
        Finish(fixture, target, "one");
        using var cts = new CancellationTokenSource();
        var poster = new FakePoster((_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var coordinator = new WakeCoordinator(store, poster, _ => { }, coalesce: TimeSpan.Zero);
        await coordinator.RunAsync(cts.Token);
        Assert.Single(poster.Attempts);
    }

    [Fact]
    public void Unread_counts_jobs_not_terminal_events()
    {
        using var fixture = new JobFixture();
        var store = new WakeStore(fixture.Database);
        var target = store.Register("codex:test", "codex", "thread", "", "/tmp");
        var jobId = Finish(fixture, target, "one");
        using (var connection = fixture.Database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO events(job_id, kind, created_at) VALUES ($id, 'needs_reconciliation', 'now')";
            command.Parameters.AddWithValue("$id", jobId);
            command.ExecuteNonQuery();
        }

        Assert.Equal(1, store.Pending().Single().Unread);
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
    public void Explicit_codex_registration_requires_a_verified_thread()
    {
        using var dir = new TempStateDir();
        var thread = Guid.NewGuid().ToString("D");
        var sessions = dir.File("sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, $"rollout-2026-09-26-{thread}.jsonl"), "");

        var request = HostSessionWake.ForCodexThread(thread, dir.Path);
        Assert.NotNull(request);
        Assert.Equal("codex:" + thread, request.WakeKey);
        Assert.Equal(thread, request.WakeAddress);
        Assert.Null(HostSessionWake.ForCodexThread(Guid.NewGuid().ToString("D"), dir.Path));
        Assert.Null(HostSessionWake.ForCodexThread(thread.ToUpperInvariant(), dir.Path));
        Assert.Null(HostSessionWake.ForCodexThread(thread, "relative-home"));
    }

    [Fact]
    public void Codex_home_comes_from_the_host_process_environment()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var dir = new TempStateDir();
        var proc = dir.File("proc");
        var host = Path.Combine(proc, "123");
        Directory.CreateDirectory(host);
        var environ = Path.Combine(host, "environ");
        File.WriteAllBytes(environ, Encoding.UTF8.GetBytes("HOME=/trusted\0CODEX_HOME=/trusted/custom\0"));
        Assert.Equal("/trusted/custom", HostSessionWake.CodexHome(123, proc));
        File.WriteAllBytes(environ, Encoding.UTF8.GetBytes("HOME=/trusted\0"));
        Assert.Equal("/trusted/.codex", HostSessionWake.CodexHome(123, proc));
        File.Delete(environ);
        Assert.Null(HostSessionWake.CodexHome(123, proc));
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
