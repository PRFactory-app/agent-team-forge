using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Features.Sessions;
using AgentTeamForge.DAL.Features.External;
using AgentTeamForge.Business.Features.External;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Features.Wake;
using AgentTeamForge.Host.Transport;
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

    static string Park(JobFixture fixture, string leadSessionId, string key)
    {
        var accepted = fixture.Accept().Execute(new SubmitJobRequest(key, "work", null, false)
        { LeadSessionId = leadSessionId });
        Assert.Equal("accepted", accepted.Outcome);
        var claim = fixture.Store.BeginNextAttempt()!;
        Assert.True(fixture.Store.EndUnsuccessfully(
            new(claim.Job.JobId, claim.RunId, claim.Generation, claim.Correlation),
            JobStatus.NeedsReconciliation, "interactive_completion_unobserved"));
        return claim.Job.JobId;
    }

    [Fact]
    public async Task Interactive_park_notifies_once()
    {
        using var fixture = new JobFixture();
        var wake = new WakeStore(fixture.Database);
        var sessions = new LeadSessionStore(fixture.Database);
        var lead = sessions.Start("/workspace/park", "parent=1");
        var target = wake.Register("codex:park", "codex", "thread", "", "/tmp");
        sessions.BindWake(lead.SessionId, target.Key, target.Generation);
        Park(fixture, lead.SessionId, "park-once");
        Assert.Empty(wake.Pending());

        var poster = new FakePoster();
        var coordinator = new WakeCoordinator(wake, poster, _ => { }, coalesce: TimeSpan.Zero);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);

        Assert.Single(poster.Attempts);
        Assert.Contains("idle without a native completion", poster.Attempts[0].Notice);
        Assert.Empty(wake.PendingParks());
    }

    [Fact]
    public void Sessionless_park_keeps_terminal_notice()
    {
        using var fixture = new JobFixture();
        var wake = new WakeStore(fixture.Database);
        var target = wake.Register("codex:nosession", "codex", "thread", "", "/tmp");
        var accepted = fixture.Accept().Execute(new SubmitJobRequest("park-nosession", "work", null, false)
        { WakeKey = target.Key, WakeGeneration = target.Generation });
        Assert.Equal("accepted", accepted.Outcome);
        var claim = fixture.Store.BeginNextAttempt()!;
        Assert.True(fixture.Store.EndUnsuccessfully(
            new(claim.Job.JobId, claim.RunId, claim.Generation, claim.Correlation),
            JobStatus.NeedsReconciliation, "interactive_completion_unobserved"));

        Assert.Equal(1, Assert.Single(wake.Pending()).Unread);
        Assert.Empty(wake.PendingParks());
    }

    [Fact]
    public async Task Registration_after_interactive_park_catches_up()
    {
        using var fixture = new JobFixture();
        var wake = new WakeStore(fixture.Database);
        var sessions = new LeadSessionStore(fixture.Database);
        var lead = sessions.Start("/workspace/park", "parent=2");
        Park(fixture, lead.SessionId, "park-before-registration");
        Assert.Empty(wake.PendingParks());

        var target = wake.Register("codex:late", "codex", "thread", "", "/tmp");
        sessions.BindWake(lead.SessionId, target.Key, target.Generation);
        var poster = new FakePoster();
        await new WakeCoordinator(wake, poster, _ => { }, coalesce: TimeSpan.Zero)
            .TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(target, Assert.Single(poster.Attempts).Target);
    }

    [Fact]
    public async Task Acknowledged_park_stays_quiet_after_reregistration_and_restart()
    {
        using var fixture = new JobFixture();
        var wake = new WakeStore(fixture.Database);
        var sessions = new LeadSessionStore(fixture.Database);
        var lead = sessions.Start("/workspace/park", "parent=3");
        var first = wake.Register("codex:park", "codex", "old", "", "/tmp");
        sessions.BindWake(lead.SessionId, first.Key, first.Generation);
        Park(fixture, lead.SessionId, "park-acked");
        var firstPoster = new FakePoster();
        await new WakeCoordinator(wake, firstPoster, _ => { }, coalesce: TimeSpan.Zero)
            .TickAsync(TestContext.Current.CancellationToken);
        Assert.Single(firstPoster.Attempts);

        var second = wake.Register("codex:new", "codex", "new", "", "/tmp");
        sessions.BindWake(lead.SessionId, second.Key, second.Generation);
        var reopened = AgentTeamForge.DAL.Sqlite.JobDatabase.Open(fixture.Database.Path, TimeSpan.FromSeconds(2));
        var restarted = new WakeStore(reopened);
        Assert.Empty(restarted.PendingParks());
        var poster = new FakePoster();
        await new WakeCoordinator(restarted, poster, _ => { }, coalesce: TimeSpan.Zero)
            .TickAsync(TestContext.Current.CancellationToken);
        Assert.Empty(poster.Attempts);
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
        store.MarkRead(job.JobId, JobStatus.Cancelled, target.Key, target.Generation);
        Assert.Empty(store.Pending());
    }

    [Fact]
    public void Completion_racing_a_read_of_the_running_job_stays_unread()
    {
        using var fixture = new JobFixture();
        var store = new WakeStore(fixture.Database);
        var target = store.Register("codex:race", "codex", "thread", "", "/tmp");
        var job = fixture.Accept().Execute(new SubmitJobRequest("race", "work", null, false) { WakeKey = target.Key, WakeGeneration = target.Generation }).Job!;
        var claim = fixture.Store.BeginNextAttempt()!;
        var observed = fixture.Get().Execute(job.JobId).Job!; // get_job/list_jobs saw it running...

        Assert.True(fixture.Store.Complete(new(claim.Job.JobId, claim.RunId, claim.Generation, claim.Correlation), "done"));
        store.MarkRead(job.JobId, observed.Status, target.Key, target.Generation); // ...and marks after it completed.

        Assert.Equal(JobStatus.Running, observed.Status);
        Assert.Equal(1, Assert.Single(store.Pending()).Unread);
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
        Assert.Contains("2 job(s)", poster.Attempts[0].Notice);
        Assert.Contains("list_jobs", poster.Attempts[0].Notice);
        Assert.Contains("unread=true", poster.Attempts[0].Notice);
        Assert.Contains("get_job", poster.Attempts[0].Notice);
        time += TimeSpan.FromSeconds(10);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        Assert.Single(poster.Attempts);
        store.MarkRead(first, JobStatus.Completed, target.Key, target.Generation);
        store.MarkRead(second, JobStatus.Completed, target.Key, target.Generation);
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
        var failures = new List<string>();
        var coordinator = new WakeCoordinator(store, poster, failures.Add, () => time, TimeSpan.Zero);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        Assert.Single(poster.Attempts);
        Assert.Contains(failures, line => line.Contains("wake post rejected:") && line.Contains("source=jobs"));
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
    public async Task Legacy_lead_wake_is_rebound_to_a_live_bridge_for_external_messages_and_jobs()
    {
        using var fixture = new JobFixture();
        var wake = new WakeStore(fixture.Database);
        var sessions = new LeadSessionStore(fixture.Database);
        var lead = sessions.Start("/workspace/wake", "parent=1");
        var team = new ExternalTeam(new ExternalMemberStore(fixture.Database), wake);
        var legacy = wake.Register("claude:/tmp/123.sock", "claude", "/tmp/123.sock", "", "");
        sessions.BindWake(lead.SessionId, legacy.Key, legacy.Generation);
        var ticket = team.CreateTicket(lead.SessionId, lead.Workspace, "visitor", null).Ticket!;
        var member = team.Join(lead.SessionId, ticket.Token).Member!;
        Assert.True(team.Send(member.MemberToken, "reply").Ok);
        var job = fixture.Accept().Execute(new SubmitJobRequest("legacy-job", "work", null, false)
        { LeadSessionId = lead.SessionId, WakeKey = legacy.Key, WakeGeneration = legacy.Generation });
        Assert.True(job.Error is null, job.Error);
        var claim = fixture.Store.BeginNextAttempt()!;
        Assert.True(fixture.Store.Complete(new(claim.Job.JobId, claim.RunId, claim.Generation, claim.Correlation), "done"));

        var target = new IpcRequest
        {
            Op = IpcProtocol.WakeRegister,
            WakeKey = "claude:hash",
            WakeKind = "claude",
            WakeAddress = legacy.Address,
            WakeSecret = "token",
            WakeHome = "123"
        };
        var oldStatus = wake.Status(lead.SessionId);
        Assert.False(oldStatus.Usable);
        Assert.Equal(JobsMcpBridge.WakeRepair.Register, JobsMcpBridge.RepairWake(oldStatus, target, legacy.Generation));
        var current = wake.Register(target.WakeKey!, target.WakeKind!, target.WakeAddress!, target.WakeSecret!, target.WakeHome!);
        sessions.BindWake(lead.SessionId, current.Key, current.Generation);
        Assert.Equal(current.Key, wake.Status(lead.SessionId).Key);
        Assert.Equal(JobsMcpBridge.WakeRepair.Keep, JobsMcpBridge.RepairWake(wake.Status(lead.SessionId), target, current.Generation));
        Assert.Equal(current.Key, Assert.Single(wake.PendingExternal()).Target.Key);
        Assert.Equal(current.Key, Assert.Single(wake.Pending()).Target.Key);
        var poster = new FakePoster();
        await new WakeCoordinator(wake, poster, _ => { }, coalesce: TimeSpan.Zero)
            .TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, poster.Attempts.Count);
        Assert.Contains(poster.Attempts, attempt => attempt.Notice.Contains("external message(s)"));
        Assert.Contains(poster.Attempts, attempt => attempt.Notice.Contains("job(s) finished"));
    }

    [Fact]
    public async Task Follow_up_acknowledges_the_completed_parent_so_it_does_not_renotify()
    {
        using var fixture = new JobFixture();
        var (endpoint, wake, lead) = FollowUpSetup(fixture);
        var parent = Submit(endpoint, lead, "p");
        var claim = fixture.Store.BeginNextAttempt()!;
        var run = new RunRef(claim.Job.JobId, claim.RunId, claim.Generation, claim.Correlation);
        Assert.True(fixture.Store.RecordSession(run, "sess"));
        Assert.True(fixture.Store.Complete(run, "done"));
        Assert.Equal(1, Assert.Single(wake.Pending()).Unread);

        var followed = endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobFollowUp,
            LeadSessionId = lead.SessionId,
            Workspace = lead.Workspace,
            JobId = parent,
            Instruction = "next",
            IdempotencyKey = "f1"
        });
        Assert.True(followed.Ok, followed.Error);
        Assert.Empty(wake.Pending());
        var poster = new FakePoster();
        await new WakeCoordinator(wake, poster, _ => { }, coalesce: TimeSpan.Zero, renotify: TimeSpan.Zero)
            .TickAsync(TestContext.Current.CancellationToken);
        Assert.Empty(poster.Attempts);
    }

    [Fact]
    public void Reading_a_follow_up_acknowledges_a_parent_that_finished_after_it_was_queued()
    {
        using var fixture = new JobFixture();
        var (endpoint, wake, lead) = FollowUpSetup(fixture);
        var parent = Submit(endpoint, lead, "p");
        var claim = fixture.Store.BeginNextAttempt()!;
        var run = new RunRef(claim.Job.JobId, claim.RunId, claim.Generation, claim.Correlation);
        Assert.True(fixture.Store.RecordSession(run, "sess"));
        var followed = endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobFollowUp,
            LeadSessionId = lead.SessionId,
            Workspace = lead.Workspace,
            JobId = parent,
            Instruction = "next",
            IdempotencyKey = "f1",
            Defer = true
        });
        Assert.True(followed.Ok, followed.Error);
        Assert.Empty(wake.Pending());
        Assert.True(endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobGet,
            LeadSessionId = lead.SessionId,
            Workspace = lead.Workspace,
            JobId = followed.Job!.JobId
        }).Ok);
        Assert.True(fixture.Store.Complete(run, "done"));
        Assert.Equal(1, Assert.Single(wake.Pending()).Unread);

        Assert.True(endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobGet,
            LeadSessionId = lead.SessionId,
            Workspace = lead.Workspace,
            JobId = followed.Job!.JobId
        }).Ok);
        Assert.Empty(wake.Pending());
    }

    [Fact]
    public void Refused_follow_up_does_not_acknowledge_the_parent()
    {
        using var fixture = new JobFixture();
        var (endpoint, wake, lead) = FollowUpSetup(fixture);
        var parent = Submit(endpoint, lead, "p");
        var claim = fixture.Store.BeginNextAttempt()!;
        Assert.True(fixture.Store.Complete(new RunRef(claim.Job.JobId, claim.RunId, claim.Generation, claim.Correlation), "done"));
        Assert.Equal(1, Assert.Single(wake.Pending()).Unread);

        var refused = endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobFollowUp,
            LeadSessionId = lead.SessionId,
            Workspace = lead.Workspace,
            JobId = parent,
            Instruction = "next",
            IdempotencyKey = "f1"
        });

        Assert.False(refused.Ok);
        Assert.Equal(JobErrors.ParentNotReady, refused.Error);
        Assert.Equal(1, Assert.Single(wake.Pending()).Unread);
    }

    [Fact]
    public void Another_lead_reading_the_child_does_not_acknowledge_its_parent()
    {
        using var fixture = new JobFixture();
        var (endpoint, wake, lead) = FollowUpSetup(fixture);
        var parent = Submit(endpoint, lead, "p");
        var claim = fixture.Store.BeginNextAttempt()!;
        var run = new RunRef(claim.Job.JobId, claim.RunId, claim.Generation, claim.Correlation);
        Assert.True(fixture.Store.RecordSession(run, "sess"));
        var followed = endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobFollowUp,
            LeadSessionId = lead.SessionId,
            Workspace = lead.Workspace,
            JobId = parent,
            Instruction = "next",
            IdempotencyKey = "f1",
            Defer = true
        });
        Assert.True(followed.Ok, followed.Error);
        Assert.True(fixture.Store.Complete(run, "done"));
        Assert.Equal(1, Assert.Single(wake.Pending()).Unread);

        var other = new LeadSessionStore(fixture.Database).Start(lead.Workspace, "other=1");
        var target = wake.Status(lead.SessionId);
        Assert.True(endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.SessionBindWake,
            LeadSessionId = other.SessionId,
            Workspace = other.Workspace,
            WakeKey = target.Key,
            WakeGeneration = target.Generation
        }).Ok);
        Assert.True(endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobGet,
            LeadSessionId = other.SessionId,
            Workspace = other.Workspace,
            JobId = followed.Job!.JobId
        }).Ok);
        Assert.Equal(1, Assert.Single(wake.Pending()).Unread);
    }

    static (JobsEndpoint Endpoint, WakeStore Wake, LeadSessionInfo Lead) FollowUpSetup(JobFixture fixture)
    {
        var wake = new WakeStore(fixture.Database);
        var sessions = new LeadSessionStore(fixture.Database);
        var lead = sessions.Start("/workspace/wake-followup", "parent=1");
        var accept = fixture.Accept();
        var endpoint = new JobsEndpoint(accept, fixture.Get(), new FollowUpJob(fixture.Store, JobFixture.Operator, accept), fixture.List(),
            new StopJob(fixture.Store, JobFixture.Operator, _ => { }), new AgentTeamForge.DAL.Sqlite.DurabilityCheckpoints(null),
            () => { }, wake, jobStore: fixture.Store, sessions: sessions);
        var registration = wake.Register("codex:followup", "codex", "addr", "", "/tmp");
        Assert.True(endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.SessionBindWake,
            LeadSessionId = lead.SessionId,
            Workspace = lead.Workspace,
            WakeKey = registration.Key,
            WakeGeneration = registration.Generation
        }).Ok);
        return (endpoint, wake, lead);
    }

    static string Submit(JobsEndpoint endpoint, LeadSessionInfo lead, string key)
    {
        var submitted = endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobSubmit,
            LeadSessionId = lead.SessionId,
            Workspace = lead.Workspace,
            Backend = "fake",
            IdempotencyKey = key,
            Instruction = key
        });
        Assert.True(submitted.Ok, submitted.Error);
        return submitted.Job!.JobId;
    }

    [Fact]
    public async Task Resume_preserves_read_jobs_and_get_stop_list_drain_unread_completions()
    {
        using var fixture = new JobFixture();
        var wake = new WakeStore(fixture.Database);
        var sessions = new LeadSessionStore(fixture.Database);
        var lead = sessions.Start(Path.GetFullPath("/workspace/wake-read"), "parent=1");
        var accept = fixture.Accept();
        var endpoint = new JobsEndpoint(accept, fixture.Get(), new FollowUpJob(fixture.Store, JobFixture.Operator, accept), fixture.List(),
            new StopJob(fixture.Store, JobFixture.Operator, _ => { }), new AgentTeamForge.DAL.Sqlite.DurabilityCheckpoints(null),
            () => { }, wake, jobStore: fixture.Store, sessions: sessions,
            stopAgent: new StopAgent(fixture.Store, JobFixture.Operator, new BackendCatalog()));

        IpcResponse Submit(string key) => endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobSubmit,
            LeadSessionId = lead.SessionId,
            Workspace = lead.Workspace,
            Backend = "fake",
            IdempotencyKey = key,
            Instruction = key
        });

        void Complete(string jobId)
        {
            var claim = fixture.Store.BeginNextAttempt()!;
            Assert.Equal(jobId, claim.Job.JobId);
            Assert.True(fixture.Store.Complete(new(claim.Job.JobId, claim.RunId, claim.Generation, claim.Correlation), "done"));
        }

        var beforeWake = Submit("before-wake");
        Assert.True(beforeWake.Ok, beforeWake.Error);
        Complete(beforeWake.Job!.JobId);

        var first = wake.Register("codex:before-restart", "codex", "old", "", "/tmp");
        Assert.True(endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.SessionBindWake,
            LeadSessionId = lead.SessionId,
            Workspace = lead.Workspace,
            WakeKey = first.Key,
            WakeGeneration = first.Generation
        }).Ok);
        Assert.Empty(wake.Pending()); // A terminal job predating wake binding is already known history.

        var getBeforeResume = Submit("get-before-resume");
        var getAfterResume = Submit("get-after-resume");
        var stopAgent = Submit("stop-agent");
        var listJobs = Submit("list-jobs");
        foreach (var submitted in new[] { getBeforeResume, getAfterResume, stopAgent, listJobs })
        {
            Assert.True(submitted.Ok, submitted.Error);
            Complete(submitted.Job!.JobId);
        }
        Assert.Equal(4, Assert.Single(wake.Pending()).Unread);

        Assert.True(endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobGet,
            LeadSessionId = lead.SessionId,
            Workspace = lead.Workspace,
            JobId = getBeforeResume.Job!.JobId
        }).Ok);
        Assert.Equal(3, Assert.Single(wake.Pending()).Unread);

        var resumed = endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.SessionResume,
            LeadSessionId = lead.SessionId,
            Workspace = lead.Workspace,
            BindingKey = "parent=restart"
        });
        Assert.True(resumed.Ok, resumed.Error);
        var current = wake.Register("codex:after-restart", "codex", "new", "", "/tmp");
        Assert.True(endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.SessionBindWake,
            LeadSessionId = lead.SessionId,
            Workspace = lead.Workspace,
            WakeKey = current.Key,
            WakeGeneration = current.Generation
        }).Ok);
        Assert.Equal(3, Assert.Single(wake.Pending()).Unread); // Resume must not resurrect the read job.

        Assert.True(endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobGet,
            LeadSessionId = lead.SessionId,
            Workspace = lead.Workspace,
            JobId = getAfterResume.Job!.JobId
        }).Ok);
        Assert.Equal(2, Assert.Single(wake.Pending()).Unread);

        var stopped = endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobStopAgent,
            LeadSessionId = lead.SessionId,
            Workspace = lead.Workspace,
            JobId = stopAgent.Job!.JobId
        });
        Assert.True(stopped.Ok, stopped.Error);
        Assert.Equal(1, Assert.Single(wake.Pending()).Unread);

        var refused = endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobStopAgent,
            LeadSessionId = lead.SessionId,
            Workspace = lead.Workspace,
            JobId = "job_missing"
        });
        Assert.False(refused.Ok);
        Assert.False(string.IsNullOrEmpty(refused.ErrorDetail));

        var listed = endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobList,
            LeadSessionId = lead.SessionId,
            Workspace = lead.Workspace
        });
        Assert.True(listed.Ok, listed.Error);
        Assert.Contains(listed.Page!.Jobs, job => job.JobId == listJobs.Job!.JobId);
        Assert.Empty(wake.Pending());

        var newJob = Submit("after-read");
        Assert.True(newJob.Ok, newJob.Error);
        Complete(newJob.Job!.JobId);
        var poster = new FakePoster();
        await new WakeCoordinator(wake, poster, _ => { }, coalesce: TimeSpan.Zero)
            .TickAsync(TestContext.Current.CancellationToken);
        Assert.Contains("1 job(s)", Assert.Single(poster.Attempts).Notice);
    }

    [Fact]
    public void Wake_repair_adopts_its_own_generation_and_never_takes_another_live_binding()
    {
        var mine = new IpcRequest { Op = IpcProtocol.WakeRegister, WakeKey = "claude:a", WakeKind = "claude", WakeAddress = "/tmp/a.sock" };
        static WakeRegistrationStatus Bound(string key, long generation, string address, bool usable = true) =>
            new(true, key, generation, "claude", address, usable);
        Assert.Equal(JobsMcpBridge.WakeRepair.Keep, JobsMcpBridge.RepairWake(Bound("claude:a", 3, "/tmp/a.sock"), mine, 3));
        Assert.Equal(JobsMcpBridge.WakeRepair.Adopt, JobsMcpBridge.RepairWake(Bound("claude:a", 4, "/tmp/a.sock"), mine, 3));
        // Another bridge resumed this session on its own channel: do not steal it back on every call.
        Assert.Equal(JobsMcpBridge.WakeRepair.Keep, JobsMcpBridge.RepairWake(Bound("claude:b", 1, "/tmp/b.sock"), mine, 3));
        // Our own channel under a legacy key, a dead registration or no binding is repaired.
        Assert.Equal(JobsMcpBridge.WakeRepair.Register, JobsMcpBridge.RepairWake(Bound("claude:/tmp/a.sock", 1, "/tmp/a.sock"), mine, 3));
        Assert.Equal(JobsMcpBridge.WakeRepair.Register, JobsMcpBridge.RepairWake(Bound("claude:b", 1, "/tmp/b.sock", usable: false), mine, 3));
        Assert.Equal(JobsMcpBridge.WakeRepair.Register, JobsMcpBridge.RepairWake(new(false, null, null, null, null), mine, 3));
    }

    [Fact]
    public void Jobs_follow_the_sessions_current_wake_binding_not_a_stale_bridge_generation()
    {
        using var fixture = new JobFixture();
        var wake = new WakeStore(fixture.Database);
        var sessions = new LeadSessionStore(fixture.Database);
        var lead = sessions.Start("/workspace/wake", "parent=1");
        var accept = fixture.Accept();
        var endpoint = new JobsEndpoint(accept, fixture.Get(), new FollowUpJob(fixture.Store, JobFixture.Operator, accept), fixture.List(),
            new StopJob(fixture.Store, JobFixture.Operator, _ => { }), new AgentTeamForge.DAL.Sqlite.DurabilityCheckpoints(null),
            () => { }, wake, jobStore: fixture.Store, sessions: sessions);
        var stale = wake.Register("claude:a", "claude", "/tmp/a.sock", "token", "123");
        var current = wake.Register("claude:a", "claude", "/tmp/a.sock", "token", "123");
        sessions.BindWake(lead.SessionId, current.Key, current.Generation);
        var submitted = endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobSubmit,
            LeadSessionId = lead.SessionId,
            Workspace = lead.Workspace,
            Backend = "fake",
            IdempotencyKey = "stale-wake",
            Instruction = "work",
            WakeKey = stale.Key,
            WakeGeneration = stale.Generation
        });
        Assert.True(submitted.Ok, submitted.Error);
        var claim = fixture.Store.BeginNextAttempt()!;
        Assert.True(fixture.Store.Complete(new(claim.Job.JobId, claim.RunId, claim.Generation, claim.Correlation), "done"));
        Assert.Equal(current.Key, Assert.Single(wake.Pending()).Target.Key);
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
        store.MarkRead(jobId, JobStatus.Completed, old.Key, old.Generation);
        Assert.Equal(1, store.Pending().Single().Unread);
        store.MarkRead(jobId, JobStatus.Completed, current.Key, current.Generation);
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
        // Verbatim codex-cli 0.157.1 stdout.
        const string real = "Queued message 01a0e232-13f4-7e00-9ca4-31d3154ede83 for thread 01a0e231-f050-7442-9454-9252aad0242f.\n";
        Assert.True(CodexQueueWake.HasSubmissionId(real, "01a0e231-f050-7442-9454-9252aad0242f"));
        Assert.False(CodexQueueWake.HasSubmissionId(real, "11111111-1111-4111-8111-111111111111"));
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
    static (WakeStore Wake, LeadSessionStore Sessions, string LeadId, WakeRegistration Target) LeadWithWake(JobFixture fixture, string name)
    {
        var wake = new WakeStore(fixture.Database);
        var sessions = new LeadSessionStore(fixture.Database);
        var lead = sessions.Start("/workspace/" + name, "parent=1");
        var target = wake.Register("codex:" + name, "codex", "thread", "", "/tmp");
        sessions.BindWake(lead.SessionId, target.Key, target.Generation);
        return (wake, sessions, lead.SessionId, target);
    }

    static (string JobId, RunRef Run) LeadJob(JobFixture fixture, string leadId, WakeRegistration target, string key)
    {
        var job = fixture.Accept().Execute(new SubmitJobRequest(key, "work", null, false)
        { LeadSessionId = leadId, WakeKey = target.Key, WakeGeneration = target.Generation }).Job!;
        var claim = fixture.Store.BeginNextAttempt()!;
        return (job.JobId, new RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation));
    }

    [Fact]
    public void Read_while_wake_is_cleared_survives_reregistration()
    {
        using var fixture = new JobFixture();
        var (wake, sessions, leadId, target) = LeadWithWake(fixture, "cleared");
        var (jobId, run) = LeadJob(fixture, leadId, target, "cleared");
        Assert.True(fixture.Store.Complete(run, "done"));
        Assert.True(wake.ClearLead(leadId, target.Key, target.Generation));

        wake.MarkReadForLead(jobId, JobStatus.Completed, null, leadId);
        var again = wake.Register(target.Key, "codex", "thread", "", "/tmp");
        sessions.BindWake(leadId, again.Key, again.Generation);

        Assert.Empty(wake.Pending());
    }

    [Fact]
    public async Task New_completion_wakes_after_coalesce_while_an_older_job_is_unread()
    {
        using var fixture = new JobFixture();
        var store = new WakeStore(fixture.Database);
        var target = store.Register("codex:new", "codex", "thread", "", "/tmp");
        Finish(fixture, target, "old");
        var time = DateTimeOffset.UtcNow;
        var poster = new FakePoster();
        var coordinator = new WakeCoordinator(store, poster, _ => { }, () => time, TimeSpan.Zero);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        Assert.Single(poster.Attempts);

        Finish(fixture, target, "new");
        time += TimeSpan.FromSeconds(1);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, poster.Attempts.Count);
        Assert.Contains("2 job(s)", poster.Attempts[1].Notice);
    }

    [Fact]
    public async Task Renotify_backs_off_for_an_unchanged_unread_set()
    {
        using var fixture = new JobFixture();
        var store = new WakeStore(fixture.Database);
        var target = store.Register("codex:backoff", "codex", "thread", "", "/tmp");
        Finish(fixture, target, "one");
        var time = DateTimeOffset.UtcNow;
        var poster = new FakePoster();
        var coordinator = new WakeCoordinator(store, poster, _ => { }, () => time, TimeSpan.Zero);
        async Task Tick(TimeSpan advance)
        {
            time += advance;
            await coordinator.TickAsync(TestContext.Current.CancellationToken);
        }

        await Tick(TimeSpan.Zero);
        Assert.Single(poster.Attempts);
        await Tick(TimeSpan.FromMinutes(5));
        Assert.Equal(2, poster.Attempts.Count);
        await Tick(TimeSpan.FromMinutes(5)); // next reminder is due after 10 minutes
        Assert.Equal(2, poster.Attempts.Count);
        await Tick(TimeSpan.FromMinutes(5));
        Assert.Equal(3, poster.Attempts.Count);
    }

    [Fact]
    public async Task Job_read_while_needs_reconciliation_wakes_again_when_it_completes()
    {
        using var fixture = new JobFixture();
        var (wake, _, leadId, target) = LeadWithWake(fixture, "rearm");
        var (jobId, run) = LeadJob(fixture, leadId, target, "rearm");
        Assert.True(fixture.Store.RecordSession(run, "native"));
        fixture.Store.RecordStartup(run, "submitted");
        Assert.Equal([jobId], fixture.Store.QuarantineUncertainAttempts());
        var observed = fixture.Store.GetJob(jobId)!;
        wake.MarkReadForLead(jobId, observed.Status, observed.Revision, leadId);
        Assert.Empty(wake.Pending());

        Assert.True(fixture.Store.ReattachQuarantined(run));
        Assert.True(fixture.Store.Complete(run, "done"));
        // The lead observed needs_reconciliation before the completion; that stale read must not consume it.
        wake.MarkReadForLead(jobId, observed.Status, observed.Revision, leadId);
        wake.MarkReadForLead(jobId, JobStatus.Completed, observed.Revision, leadId);
        Assert.Equal(1, Assert.Single(wake.Pending()).Unread);

        var poster = new FakePoster();
        await new WakeCoordinator(wake, poster, _ => { }, coalesce: TimeSpan.Zero).TickAsync(TestContext.Current.CancellationToken);
        Assert.Single(poster.Attempts);
    }

    [Fact]
    public void List_jobs_unread_returns_only_unread_finished_jobs_regardless_of_since()
    {
        using var fixture = new JobFixture();
        var (wake, _, leadId, target) = LeadWithWake(fixture, "unread");
        var (readId, readRun) = LeadJob(fixture, leadId, target, "read");
        Assert.True(fixture.Store.Complete(readRun, "done"));
        var (unreadId, unreadRun) = LeadJob(fixture, leadId, target, "unread");
        Assert.True(fixture.Store.Complete(unreadRun, "done"));
        var (runningId, _) = LeadJob(fixture, leadId, target, "running");
        wake.MarkReadForLead(readId, JobStatus.Completed, null, leadId);

        var list = new ListJobs(fixture.Store, JobFixture.Operator);
        var page = list.Execute(new ListJobsRequest(Since: DateTimeOffset.UtcNow.AddHours(1).ToString("O")) { Unread = true, LeadSessionId = leadId });
        Assert.Equal(unreadId, Assert.Single(page.Page!.Jobs).JobId);
        var unreadOnly = list.Execute(new ListJobsRequest { Unread = true, LeadSessionId = leadId }).Page!.Jobs;
        Assert.Equal(unreadId, Assert.Single(unreadOnly).JobId);
        Assert.True(unreadOnly[0].Unread);
        Assert.DoesNotContain(list.Execute(new ListJobsRequest { LeadSessionId = leadId }).Page!.Jobs, j => j.JobId == runningId && j.Unread);
    }
    [Fact]
    public async Task New_completion_wakes_after_coalesce_even_at_the_retry_cap()
    {
        using var fixture = new JobFixture();
        var store = new WakeStore(fixture.Database);
        var target = store.Register("codex:cap", "codex", "thread", "", "/tmp");
        Finish(fixture, target, "old");
        var time = DateTimeOffset.UtcNow;
        var reachable = false;
        var poster = new FakePoster((_, _) => reachable);
        var coordinator = new WakeCoordinator(store, poster, _ => { }, () => time);
        for (var i = 0; i < 10; i++)
        {
            time += TimeSpan.FromSeconds(301);
            await coordinator.TickAsync(TestContext.Current.CancellationToken);
        }
        var failed = poster.Attempts.Count;
        Assert.True(failed >= 8);

        Finish(fixture, target, "new");
        reachable = true;
        time += TimeSpan.FromSeconds(1);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(failed, poster.Attempts.Count); // coalesce window still open
        time += TimeSpan.FromSeconds(2);
        await coordinator.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(failed + 1, poster.Attempts.Count);
        Assert.Contains("2 job(s)", poster.Attempts[^1].Notice);
    }
}
