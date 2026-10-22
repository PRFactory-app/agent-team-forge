using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.Tests.Features.Jobs;

/// <summary>A locked database delays or fails one attempt retryably; it never halts the dispatcher.</summary>
public sealed class DispatchBusyTests
{
    static ScriptedBackend Done() => new(r => [new BackendEvidence.Result(r.Correlation, "done")]);

    static async Task<DispatchJob> Dispatch(JobFixture f, JobStore store, ScriptedBackend backend, Func<Task> scenario,
        DurabilityCheckpoints? checkpoints = null, Action<string>? log = null)
    {
        var dispatcher = new DispatchJob(store, backend, f.Limits, checkpoints ?? DurabilityCheckpoints.None, f.Admission, log ?? (_ => { }));
        using var lifetime = new CancellationTokenSource(Bounded.ScenarioDeadline);
        var dispatching = dispatcher.RunAsync(lifetime.Token);
        try { await scenario(); }
        finally
        {
            await lifetime.CancelAsync();
            await dispatching;
        }
        return dispatcher;
    }

    [Fact]
    public async Task Completion_write_succeeds_after_a_real_lock_is_released()
    {
        using var f = new JobFixture(new SpikeLimits { MaxConcurrentJobs = 1, BusyTimeout = TimeSpan.FromMilliseconds(100) });
        var job = f.Submit("k1");
        using var holder = new SqliteConnection($"Data Source={f.DatabasePath};Pooling=False");
        holder.Open();
        SqliteTransaction? lockTx = null;
        var backend = new ScriptedBackend(r => [new BackendEvidence.Result(r.Correlation, "done")])
        {
            // Another writer holds the write lock across the completion write, then lets go.
            OnStart = request =>
            {
                lockTx = holder.BeginTransaction(deferred: false);
                _ = Task.Delay(TimeSpan.FromMilliseconds(2500)).ContinueWith(_ => lockTx!.Rollback(), TaskScheduler.Default);
            }
        };
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var dispatcher = await Dispatch(f, f.Store, backend,
            () => Bounded.Until(() => f.Store.GetJob(job.JobId)!.Status == JobStatus.Completed, "completed after the lock"),
            log: logs.Enqueue);

        Assert.Equal("done", f.Store.GetJob(job.JobId)!.ResultText);
        Assert.Contains(logs, line => line.Contains("database busy; retry"));
        Assert.Null(dispatcher.HaltReason);
    }

    [Fact]
    public async Task Busy_completion_write_retries_then_commits()
    {
        using var f = new JobFixture(new SpikeLimits { MaxConcurrentJobs = 1 });
        var busy = 2;
        var store = new JobStore(f.Database, new DurabilityCheckpoints(point =>
        {
            if (point == DurabilityCheckpoints.CompleteBeforeCommit && Interlocked.Decrement(ref busy) >= 0)
            {
                throw new StorageException(StorageFailure.Busy, "database busy");
            }
        }));
        var job = f.Submit("k1");
        var dispatcher = await Dispatch(f, store, Done(),
            () => Bounded.Until(() => f.Store.GetJob(job.JobId)!.Status == JobStatus.Completed, "completed after retries"));

        Assert.Equal("done", f.Store.GetJob(job.JobId)!.ResultText);
        Assert.Null(dispatcher.HaltReason);
    }

    [Fact]
    public async Task Exhausted_completion_write_fails_retryably_and_dispatch_goes_on()
    {
        using var f = new JobFixture(new SpikeLimits { MaxConcurrentJobs = 1 });
        var first = f.Submit("k1");
        var second = f.Submit("k2");
        var store = new JobStore(f.Database, new DurabilityCheckpoints(point =>
        {
            if (point == DurabilityCheckpoints.CompleteBeforeCommit && f.Store.GetJob(first.JobId)!.Status == JobStatus.Running)
            {
                throw new StorageException(StorageFailure.Busy, "database busy");
            }
        }));
        var dispatcher = await Dispatch(f, store, Done(), async () =>
        {
            await Bounded.Until(() => f.Store.GetJob(second.JobId)!.Status == JobStatus.Completed, "next job completed");
            Assert.Equal("accepted", f.Accept().Execute(new SubmitJobRequest("k3", "x", null, true)).Outcome);
        });

        AssertRetryable(f, first.JobId);
        Assert.Null(dispatcher.HaltReason);
    }

    [Fact]
    public async Task Busy_escaping_the_attempt_fails_retryably_without_a_halt()
    {
        using var f = new JobFixture(new SpikeLimits { MaxConcurrentJobs = 1 });
        var first = f.Submit("k1");
        var second = f.Submit("k2");
        var checkpoints = new DurabilityCheckpoints(point =>
        {
            if (point == DurabilityCheckpoints.AttemptAfterCommit && f.Store.GetJob(first.JobId)!.Status == JobStatus.Running)
            {
                throw new StorageException(StorageFailure.Busy, "database busy");
            }
        });
        var dispatcher = await Dispatch(f, f.Store, Done(),
            () => Bounded.Until(() => f.Store.GetJob(second.JobId)!.Status == JobStatus.Completed, "next job completed"), checkpoints);

        AssertRetryable(f, first.JobId);
        Assert.Null(dispatcher.HaltReason);
    }

    static void AssertRetryable(JobFixture f, string jobId)
    {
        var job = f.Store.GetJob(jobId)!;
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(JobErrors.StorageBusy, job.ReasonCode);
        Assert.False(f.Store.IsSessionFenced(jobId));
    }
}
