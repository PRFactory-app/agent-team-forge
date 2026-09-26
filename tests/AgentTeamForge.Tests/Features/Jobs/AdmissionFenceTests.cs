using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Recovery;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

/// <summary>
/// Re-review F1: dispatcher halt and submission share one linearized
/// admission fence. A submission admitted before the halt may still commit
/// (queued for restart); one beginning after it is refused without a write.
/// Ordering is forced with barriers at durable checkpoints, never with sleeps.
/// </summary>
public sealed class AdmissionFenceTests
{
    [Fact]
    public async Task Submit_in_flight_before_halt_commits_and_submit_after_halt_is_refused_without_a_write()
    {
        using var f = new JobFixture();
        var first = f.Submit("k-first");

        // Dispatcher: pause right after its attempt-start commit, then fault its backend start.
        using var dispatcherAtAttempt = new ManualResetEventSlim();
        using var releaseDispatcher = new ManualResetEventSlim();
        var dispatchCheckpoints = new DurabilityCheckpoints(point =>
        {
            if (point == DurabilityCheckpoints.AttemptAfterCommit)
            {
                dispatcherAtAttempt.Set();
                releaseDispatcher.Wait(Bounded.ScenarioDeadline);
            }
        });

        // Its own connection with a short busy timeout: while the in-flight submit
        // holds the write lock, the dispatcher's terminal write fails and it halts.
        var dispatchStore = new JobStore(JobDatabase.Open(f.DatabasePath, TimeSpan.FromMilliseconds(200)), DurabilityCheckpoints.None);
        var backend = new ScriptedBackend(_ => []) { OnStart = _ => throw new InvalidOperationException("pipe exploded") };
        using var dispatcher = new DispatchJob(dispatchStore, backend, f.Limits, dispatchCheckpoints, f.Admission, _ => { });

        // In-flight submit: admitted, inside its transaction, paused before commit.
        using var submitBeforeCommit = new ManualResetEventSlim();
        using var releaseSubmit = new ManualResetEventSlim();
        var pausingStore = new JobStore(f.Database, new DurabilityCheckpoints(point =>
        {
            if (point == DurabilityCheckpoints.AcceptBeforeCommit)
            {
                submitBeforeCommit.Set();
                releaseSubmit.Wait(Bounded.ScenarioDeadline);
            }
        }));

        using var lifetime = new CancellationTokenSource(Bounded.ScenarioDeadline);
        var dispatching = Task.Run(() => dispatcher.RunAsync(lifetime.Token), TestContext.Current.CancellationToken);
        Assert.True(dispatcherAtAttempt.Wait(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken));
        var inFlight = Task.Run(() => f.Accept(pausingStore).Execute(new SubmitJobRequest("k-inflight", "x", null, false)), TestContext.Current.CancellationToken);
        Assert.True(submitBeforeCommit.Wait(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken));

        releaseDispatcher.Set();
        await dispatching.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
        Assert.Equal("terminal_write_failed", dispatcher.HaltReason);

        // Began after the halt: stable unhealthy outcome, including for an existing key.
        var afterHalt = f.Accept().Execute(new SubmitJobRequest("k-after", "y", null, false));
        var sameKeyAfterHalt = f.Accept().Execute(new SubmitJobRequest("k-first", "hello", null, false));
        Assert.Equal(JobErrors.DaemonUnhealthy, afterHalt.Error);
        Assert.Equal(JobErrors.DaemonUnhealthy, sameKeyAfterHalt.Error);
        Assert.False(f.Admission.Drained.IsCompleted, "admitted submit is still in flight");

        // Admitted before the halt: completes its durable transaction and stays queued.
        releaseSubmit.Set();
        var admitted = await inFlight.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
        Assert.Equal("accepted", admitted.Outcome);
        await f.Admission.Drained.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
        Assert.Equal(1, f.Store.CountUnattemptedIntents());
        Assert.Equal(JobStatus.Queued, f.Store.GetJob(admitted.Job!.JobId)!.Status);
        Assert.Equal(["accepted"], f.Store.GetEvents(admitted.Job.JobId).Select(e => e.Kind));
        Assert.Equal(["accepted", "attempt_started"], f.Store.GetEvents(first.JobId).Select(e => e.Kind));

        // The fence stays closed once drained: no lock contention, still no acceptance.
        Assert.Equal(JobErrors.DaemonUnhealthy, f.Accept().Execute(new SubmitJobRequest("k-after", "y", null, false)).Error);
        Assert.Equal(1, f.Store.CountUnattemptedIntents());

        // Restart: the halted attempt is quarantined, the same-key retry resolves to the
        // one queued job, and the refused key was never stored.
        new RecoverOnStartup(f.Store).Execute();
        var restarted = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, new AdmissionGate());
        var retry = restarted.Execute(new SubmitJobRequest("k-inflight", "x", null, false));
        Assert.Equal("existing", retry.Outcome);
        Assert.Equal(admitted.Job.JobId, retry.Job!.JobId);
        Assert.Equal(1, f.Store.CountUnattemptedIntents());
        Assert.Equal(JobStatus.NeedsReconciliation, f.Store.GetJob(first.JobId)!.Status);
        Assert.Equal("accepted", restarted.Execute(new SubmitJobRequest("k-after", "y", null, false)).Outcome);
    }

    [Fact]
    public async Task Dispatcher_shutdown_also_closes_admission()
    {
        using var f = new JobFixture();
        using var dispatcher = new DispatchJob(f.Store, new ScriptedBackend(_ => []), f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        using var lifetime = new CancellationTokenSource();

        var dispatching = dispatcher.RunAsync(lifetime.Token);
        await lifetime.CancelAsync();
        await dispatching.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);

        Assert.Null(dispatcher.HaltReason);
        Assert.Equal(JobErrors.DaemonUnhealthy, f.Accept().Execute(new SubmitJobRequest("k", "x", null, false)).Error);
        Assert.Equal(0, f.Store.CountUnattemptedIntents());
        await f.Admission.Drained.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
    }
}
