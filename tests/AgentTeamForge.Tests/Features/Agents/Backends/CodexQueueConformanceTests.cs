using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Recovery;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;
using static AgentTeamForge.Tests.Support.CodexQueueModel;

namespace AgentTeamForge.Tests.Features.Agents.Backends;

/// <summary>
/// Real core (DispatchJob, JobStore, RecoverOnStartup) driven through the
/// existing IJobBackend by a ScriptedBackend whose delivery is a queue add on
/// <see cref="CodexQueueModel"/>. What is asserted is the current core: an
/// uncertain queue attempt is quarantined and never redispatched. The model
/// only supplies the native-side follow-up (the queued input can still run);
/// it is not evidence of native Codex behaviour.
/// </summary>
public sealed class CodexQueueCoreQuarantineTests
{
    static readonly SpikeLimits Short = new() { MaxFakeRuntime = TimeSpan.FromMilliseconds(300) };

    static DispatchJob Dispatcher(JobFixture f, IJobBackend backend) =>
        new(f.Store, backend, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });

    /// <summary>Delivery is a queue add whose client message ID is the core correlation.</summary>
    static IEnumerable<BackendEvidence> AddThenEof(Runtime server, BackendRequest request, bool dropReply)
    {
        var reply = server.Add(request.Correlation, request.Instruction, dropReply);
        if (reply is not null)
        {
            yield return new BackendEvidence.Ack(request.Correlation);
        }

        yield return new BackendEvidence.EndOfOutput();
    }

    static void AssertQuarantinedOnce(JobFixture f, string jobId, string reason, ScriptedBackend backend)
    {
        var job = f.Store.GetJob(jobId)!;
        Assert.Equal(JobStatus.NeedsReconciliation, job.Status);
        Assert.Equal(reason, job.ReasonCode);
        Assert.Null(job.ResultText);
        Assert.Equal(1, job.Attempts);
        Assert.Single(backend.Started);
        Assert.Equal(0, f.Store.CountUnattemptedIntents());
        Assert.Null(f.Store.BeginNextAttempt());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Add_during_human_turn_then_lost_connection_is_quarantined_while_the_row_can_still_run_later(bool addReplyLost)
    {
        using var f = new JobFixture();
        var queue = new CodexQueueModel();
        var server = queue.Load("server");
        server.HumanStart("human is typing");
        var backend = new ScriptedBackend(r => AddThenEof(server, r, addReplyLost));
        var job = f.Submit("k", "queued work");

        await Dispatcher(f, backend).RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);

        AssertQuarantinedOnce(f, job.JobId, "backend_eof", backend);
        Assert.Equal(!addReplyLost, f.Store.GetRuns(job.JobId).Single().Acked);
        var correlation = backend.Started.Single().Correlation;
        Assert.Equal(0, queue.Executions(correlation));

        // Native auto-drain after the human turn runs the input the core never saw complete.
        server.EndTurn(TurnEnd.Completed);

        Assert.Equal(1, queue.Executions(correlation));
        AssertQuarantinedOnce(f, job.JobId, "backend_eof", backend);
    }

    [Fact]
    public async Task Timeout_while_queued_behind_a_human_is_not_revocation_and_is_never_redispatched()
    {
        using var f = new JobFixture(Short);
        var queue = new CodexQueueModel();
        var server = queue.Load("server");
        server.HumanStart("long human turn");
        var backend = new ScriptedBackend(r => AckedAdd(server, r)) { Hangs = true };
        var job = f.Submit("k", "queued work");

        await Dispatcher(f, backend).RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None)
            .WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);

        AssertQuarantinedOnce(f, job.JobId, "backend_timeout", backend);
        Assert.Equal(1, backend.Terminations);
        Assert.Single(queue.Rows);

        server.EndTurn(TurnEnd.Completed);

        Assert.Equal(1, queue.Executions(backend.Started.Single().Correlation));
        AssertQuarantinedOnce(f, job.JobId, "backend_timeout", backend);

        static IEnumerable<BackendEvidence> AckedAdd(Runtime server, BackendRequest request)
        {
            server.Add(request.Correlation, request.Instruction);
            yield return new BackendEvidence.Ack(request.Correlation);
        }
    }

    [Fact]
    public async Task Daemon_stop_while_waiting_is_quarantined_on_restart_and_the_native_row_survives()
    {
        using var f = new JobFixture();
        var queue = new CodexQueueModel();
        var server = queue.Load("server");
        server.HumanStart("human is typing");
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new ScriptedBackend(r => AckedAddThenSignal(server, r, waiting)) { Hangs = true };
        var job = f.Submit("k", "queued work");
        using var lifetime = new CancellationTokenSource();

        var attempt = Dispatcher(f, backend).RunAttemptAsync(f.Store.BeginNextAttempt()!, lifetime.Token);
        await waiting.Task.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
        await lifetime.CancelAsync();
        await attempt.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Running, f.Store.GetJob(job.JobId)!.Status);
        Assert.Equal([job.JobId], new RecoverOnStartup(f.NewStore()).Execute());
        AssertQuarantinedOnce(f, job.JobId, "daemon_restart_uncertain", backend);
        Assert.Single(queue.Rows);

        server.EndTurn(TurnEnd.Completed);

        Assert.Equal(1, queue.Executions(backend.Started.Single().Correlation));
        AssertQuarantinedOnce(f, job.JobId, "daemon_restart_uncertain", backend);

        static IEnumerable<BackendEvidence> AckedAddThenSignal(Runtime server, BackendRequest request, TaskCompletionSource waiting)
        {
            server.Add(request.Correlation, request.Instruction);
            yield return new BackendEvidence.Ack(request.Correlation);
            waiting.SetResult();
        }
    }

    [Fact]
    public async Task Native_crash_between_start_and_row_delete_is_quarantined_once_even_if_restart_reexecutes()
    {
        using var f = new JobFixture();
        var queue = new CodexQueueModel();
        var server = queue.Load("server");
        server.CrashAfterStartBeforeDelete = true;
        var backend = new ScriptedBackend(r => CrashingAdd(server, r));
        var job = f.Submit("k", "queued work");

        await Dispatcher(f, backend).RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);

        AssertQuarantinedOnce(f, job.JobId, "backend_eof", backend);
        var correlation = backend.Started.Single().Correlation;
        Assert.Equal(1, queue.Executions(correlation));

        // A native restart finds the surviving row and runs it again; the core cannot stop that.
        queue.Load("restarted").Resume();

        Assert.Equal(2, queue.Executions(correlation));
        AssertQuarantinedOnce(f, job.JobId, "backend_eof", backend);

        static IEnumerable<BackendEvidence> CrashingAdd(Runtime server, BackendRequest request)
        {
            var crashed = false;
            try
            {
                server.Add(request.Correlation, request.Instruction);
            }
            catch (SimulatedCrashException)
            {
                crashed = true;
            }

            Assert.True(crashed);
            yield return new BackendEvidence.EndOfOutput();
        }
    }
}

/// <summary>
/// MODEL ONLY protocol counterexamples. These assert what the source-derived
/// <see cref="CodexQueueModel"/> does, to make the application-level fences
/// concrete; a pass here proves nothing about native Codex, and the current core
/// has no queue revocation, human-observation or strict-pause capability at all.
/// </summary>
public sealed class CodexQueueModelOnlyCounterexampleTests
{
    [Fact]
    public void Human_first_add_is_persisted_waits_without_steering_and_auto_drains_violating_strict_pause()
    {
        var queue = new CodexQueueModel();
        var server = queue.Load("server");
        var human = server.HumanStart("human");

        var reply = server.Add("c1", "queued");

        Assert.NotNull(reply);
        Assert.Equal(["human"], human.Inputs);
        Assert.Equal(StartOutcome.Busy, server.ExplicitStart(reply.QueueId));
        Assert.Single(queue.Rows);
        Assert.Equal(0, queue.Executions("c1"));

        server.EndTurn(TurnEnd.Completed);

        Assert.Equal(1, queue.Executions("c1"));
        Assert.Equal(Origin.Queue, server.Active!.Origin);
        Assert.Empty(queue.Rows);
        Assert.True(queue.StrictHumanPauseViolated());
    }

    [Fact]
    public void Queue_first_starts_before_add_returns_and_a_later_human_start_steers_that_turn()
    {
        var queue = new CodexQueueModel();
        var server = queue.Load("server");

        server.Add("c1", "queued");
        var executedBeforeReplyWasUsed = queue.Executions("c1");
        var turn = server.HumanStart("human");

        Assert.Equal(1, executedBeforeReplyWasUsed);
        Assert.Equal(Origin.Queue, turn.Origin);
        Assert.Equal(["queued", "human"], turn.Inputs);
        Assert.True(queue.StrictHumanPauseViolated());
    }

    [Theory]
    [InlineData(TurnEnd.Completed, 1)]
    [InlineData(TurnEnd.Failed, 1)]
    [InlineData(TurnEnd.Interrupted, 0)]
    public void Only_an_interrupted_human_turn_leaves_the_queued_input_pending(TurnEnd end, int executions)
    {
        var queue = new CodexQueueModel();
        var server = queue.Load("server");
        server.HumanStart("human");
        server.Add("c1", "queued");

        server.EndTurn(end);

        Assert.Equal(executions, queue.Executions("c1"));
        Assert.Equal(1 - executions, queue.Rows.Count);
    }

    [Fact]
    public void Interrupted_status_holds_later_adds_and_change_wakes_until_a_real_resume()
    {
        var queue = new CodexQueueModel();
        var server = queue.Load("server");
        server.HumanStart("human");
        server.Add("c1", "queued");
        server.EndTurn(TurnEnd.Interrupted);

        server.Add("c2", "queued later");
        server.Wake();

        Assert.Null(server.Active);
        Assert.Equal(0, queue.Executions("c1"));
        Assert.Equal(0, queue.Executions("c2"));
        Assert.Equal(2, queue.Rows.Count);

        server.Resume();

        Assert.Equal(1, queue.Executions("c1"));
        Assert.Equal(0, queue.Executions("c2"));
        server.EndTurn(TurnEnd.Completed);
        Assert.Equal(1, queue.Executions("c2"));
        Assert.Empty(queue.Rows);
    }

    [Fact]
    public void Blind_retry_after_a_lost_add_reply_executes_twice_because_client_id_is_not_idempotency()
    {
        var queue = new CodexQueueModel();
        var server = queue.Load("server");
        server.HumanStart("human");

        Assert.Null(server.Add("c1", "queued", dropReply: true));
        var retry = server.Add("c1", "queued");
        server.EndTurn(TurnEnd.Completed);
        server.EndTurn(TurnEnd.Completed);

        Assert.NotNull(retry);
        Assert.Equal(2, queue.Executions("c1"));
        Assert.Equal(2, queue.History.Where(p => p.ClientMessageId == "c1").Select(p => p.QueueId).Distinct().Count());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Start_before_delete_crash_leaves_a_row_whose_later_fate_says_nothing_about_execution(bool humanActiveOnRestart)
    {
        var queue = new CodexQueueModel();
        var crashed = queue.Load("server");
        crashed.CrashAfterStartBeforeDelete = true;
        Assert.Throws<SimulatedCrashException>(() => crashed.Add("c1", "queued"));
        var queueId = Assert.Single(queue.Rows).QueueId;
        Assert.Equal(1, queue.Executions("c1"));

        var restarted = queue.Load("restarted");
        if (humanActiveOnRestart)
        {
            restarted.HumanStart("human");
            restarted.Resume();

            // deleted: true although the input was already presented before the crash.
            Assert.True(restarted.Delete(queueId));
            Assert.Equal(1, queue.Executions("c1"));
        }
        else
        {
            restarted.Resume();

            Assert.Equal(2, queue.Executions("c1"));
            Assert.False(restarted.Delete(queueId));
        }
    }

    [Fact]
    public void Revocation_by_delete_or_empty_queue_does_not_fence_a_late_or_stale_add()
    {
        var queue = new CodexQueueModel();
        var server = queue.Load("server");
        server.HumanStart("human");

        // Delayed add still in flight: the queue looks empty and nothing has run.
        var inFlight = server.SendDelayed("c1", "queued");
        Assert.Empty(queue.Rows);

        // A known row is deleted; a stale sender then re-adds the same client ID.
        var known = server.Add("c2", "queued")!;
        Assert.True(server.Delete(known.QueueId));
        var stale = server.Add("c2", "queued")!;
        var late = inFlight()!;

        Assert.NotEqual(known.QueueId, stale.QueueId);
        server.EndTurn(TurnEnd.Completed);
        server.EndTurn(TurnEnd.Completed);
        server.EndTurn(TurnEnd.Completed);

        Assert.Equal(1, queue.Executions("c1"));
        Assert.Equal(1, queue.Executions("c2"));
        Assert.Contains(queue.History, p => p.QueueId == late.QueueId);
    }

    [Fact]
    public void Two_runtimes_on_one_store_both_start_the_same_row_because_the_lock_is_process_local()
    {
        var queue = new CodexQueueModel();
        var a = queue.Load("a");
        var b = queue.Load("b");
        a.HumanStart("human");
        a.Add("c1", "queued");
        a.EndTurn(TurnEnd.Interrupted);

        var seenByA = a.Peek()!;
        var seenByB = b.Peek()!;
        Assert.Equal(StartOutcome.Started, a.StartIfIdle(seenByA));
        Assert.Equal(StartOutcome.Started, b.StartIfIdle(seenByB));

        Assert.True(a.Delete(seenByA.QueueId));
        Assert.False(b.Delete(seenByB.QueueId));
        Assert.Equal(2, queue.Executions("c1"));
        Assert.Equal(["a", "b"], queue.History.Where(p => p.ClientMessageId == "c1").Select(p => p.Runtime));
    }
}
