using System.Runtime.CompilerServices;
using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

/// <summary>
/// Dispatcher fault containment (review B1) and the full-effect deadline
/// (review B2): backend failures stay local while dispatcher faults halt, and a stalled start
/// or delivery is bounded by the same maximum runtime as evidence reading.
/// </summary>
public sealed class DispatchFaultTests
{
    static readonly SpikeLimits Short = new() { MaxFakeRuntime = TimeSpan.FromMilliseconds(300) };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Launch_failure_is_contained_and_next_job_dispatches(bool noEffects)
    {
        using var f = new JobFixture(new SpikeLimits { MaxConcurrentJobs = 1 });
        var first = f.Submit("k1");
        var second = f.Submit("k2");
        var backend = new ScriptedBackend(r => [new BackendEvidence.Result(r.Correlation, "done")])
        {
            OnStart = r =>
            {
                if (r.JobId == first.JobId)
                {
                    if (noEffects) { throw new BackendNotStartedException("agent_not_ready"); }
                    throw new InvalidOperationException("partial launch");
                }
            }
        };
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, f.Admission, logs.Enqueue);
        using var lifetime = new CancellationTokenSource(Bounded.ScenarioDeadline);
        var dispatching = dispatcher.RunAsync(lifetime.Token);
        try
        {
            await Bounded.Until(() => f.Store.GetJob(second.JobId)!.Status == JobStatus.Completed, "next job completed");
            var job = f.Store.GetJob(first.JobId)!;
            Assert.Equal(noEffects ? JobStatus.Failed : JobStatus.NeedsReconciliation, job.Status);
            Assert.Equal(noEffects ? "backend_not_started" : "launch_failed", job.ReasonCode);
            Assert.Contains(noEffects ? "agent_not_ready" : "partial launch", job.ResultText);
            Assert.Contains(logs, line => line.Contains(noEffects ? "agent_not_ready" : "partial launch"));
            Assert.Equal(!noEffects, f.Store.IsSessionFenced(first.JobId));
            Assert.Null(dispatcher.HaltReason);
            Assert.Equal("accepted", f.Accept().Execute(new SubmitJobRequest("k3", "x", null, true)).Outcome);
            Assert.Single(f.Store.GetRuns(first.JobId));
        }
        finally
        {
            await lifetime.CancelAsync();
            await dispatching;
        }
    }

    [Fact]
    public async Task Dispatcher_invariant_failure_still_halts()
    {
        using var f = new JobFixture(new SpikeLimits { MaxConcurrentJobs = 1 });
        var first = f.Submit("k1");
        var second = f.Submit("k2");
        var checkpoints = new DurabilityCheckpoints(_ => throw new InvalidOperationException("dispatcher invariant"));
        using var dispatcher = new DispatchJob(f.Store, new ScriptedBackend(_ => []), f.Limits, checkpoints, f.Admission, _ => { });
        using var lifetime = new CancellationTokenSource(Bounded.ScenarioDeadline);
        await dispatcher.RunAsync(lifetime.Token);
        Assert.False(lifetime.IsCancellationRequested);
        Assert.Equal("dispatcher_fault", dispatcher.HaltReason);
        Assert.Equal(JobStatus.NeedsReconciliation, f.Store.GetJob(first.JobId)!.Status);
        Assert.Equal(JobStatus.Queued, f.Store.GetJob(second.JobId)!.Status);
    }

    [Fact]
    public async Task Unavailable_store_still_halts_admission()
    {
        using var f = new JobFixture();
        f.Submit("k1");
        File.Delete(f.DatabasePath);
        using var dispatcher = new DispatchJob(f.Store, new ScriptedBackend(_ => []), f.Limits,
            DurabilityCheckpoints.None, f.Admission, _ => { });
        using var lifetime = new CancellationTokenSource(Bounded.ScenarioDeadline);
        await dispatcher.RunAsync(lifetime.Token);
        Assert.False(lifetime.IsCancellationRequested);
        Assert.Equal("dispatcher_fault", dispatcher.HaltReason);
        Assert.Equal(JobErrors.DaemonUnhealthy, f.Accept().Execute(new SubmitJobRequest("k2", "x", null, false)).Error);
    }

    [Fact]
    public async Task Stalled_backend_start_is_bounded_by_the_runtime_deadline_and_never_retried()
    {
        using var f = new JobFixture(Short);
        using var backend = new StallingBackend { StartStalls = true };
        var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        var job = f.Submit("k");
        var claim = f.Store.BeginNextAttempt()!;

        await dispatcher.RunAttemptAsync(claim, CancellationToken.None).WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);

        var record = f.Store.GetJob(job.JobId)!;
        Assert.Equal(JobStatus.NeedsReconciliation, record.Status);
        Assert.Equal("backend_start_timeout", record.ReasonCode);
        Assert.Null(f.Store.BeginNextAttempt());

        // A start that returns after the deadline is terminated through its
        // held handle and never receives the instruction.
        backend.ReleaseStart();
        await Bounded.Until(() => backend.Terminations == 1, "late start terminated");
        Assert.Equal(0, backend.Deliveries);
    }

    [Fact]
    public async Task Stalled_delivery_before_the_child_reads_is_bounded_and_terminates_the_owned_child()
    {
        using var f = new JobFixture(Short);
        using var backend = new StallingBackend { DeliveryStalls = true };
        var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        var job = f.Submit("k");
        var claim = f.Store.BeginNextAttempt()!;

        await dispatcher.RunAttemptAsync(claim, CancellationToken.None).WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);

        var record = f.Store.GetJob(job.JobId)!;
        Assert.Equal(JobStatus.NeedsReconciliation, record.Status);
        Assert.Equal("backend_timeout", record.ReasonCode);
        Assert.Equal(1, backend.Terminations);
        Assert.Null(f.Store.BeginNextAttempt());
    }

    [Fact]
    public async Task Failed_termination_on_timeout_still_records_backend_timeout()
    {
        using var f = new JobFixture(Short);
        using var backend = new StallingBackend { DeliveryStalls = true, TerminateThrows = true };
        var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        var job = f.Submit("k");
        var claim = f.Store.BeginNextAttempt()!;

        await dispatcher.RunAttemptAsync(claim, CancellationToken.None).WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);

        var record = f.Store.GetJob(job.JobId)!;
        Assert.Equal(JobStatus.NeedsReconciliation, record.Status);
        Assert.Equal("backend_timeout", record.ReasonCode);
        Assert.Null(dispatcher.HaltReason);
    }

    [Fact]
    public void Invalid_runtime_deadline_is_rejected_before_the_dispatcher_can_run()
    {
        using var f = new JobFixture();

        Assert.ThrowsAny<ArgumentException>(() =>
            new DispatchJob(f.Store, new StallingBackend(), new SpikeLimits { MaxFakeRuntime = TimeSpan.FromSeconds(-2) }, DurabilityCheckpoints.None, new AdmissionGate(), _ => { }));
    }

    /// <summary>Deterministic stalls at start or delivery; every stall is released on dispose.</summary>
    sealed class StallingBackend : IJobBackend, IDisposable
    {
        readonly ManualResetEventSlim _startGate = new(false);
        readonly CancellationTokenSource _disposed = new();
        readonly Lock _counts = new();

        public Exception? StartThrows { get; init; }

        public bool StartStalls { get; init; }

        public bool DeliveryStalls { get; init; }

        public bool TerminateThrows { get; init; }

        public int Terminations { get; private set; }

        public int Deliveries { get; private set; }

        public void ReleaseStart() => _startGate.Set();

        public IBackendRun Start(BackendRequest request)
        {
            if (StartThrows is { } ex)
            {
                throw ex;
            }

            if (StartStalls)
            {
                _startGate.Wait(Bounded.ScenarioDeadline);
            }

            return new Run(this);
        }

        public void Dispose()
        {
            _startGate.Set();
            _disposed.Cancel();
        }

        sealed class Run(StallingBackend owner) : IBackendRun
        {
            public int? ProcessId => 4242;

            public async Task DeliverAsync(CancellationToken cancellationToken)
            {
                lock (owner._counts)
                {
                    owner.Deliveries++;
                }

                if (owner.DeliveryStalls)
                {
                    // Ignores the token, like a blocked pipe write, until the owned child is killed.
                    await Task.Delay(Timeout.Infinite, owner._disposed.Token).ContinueWith(_ => { }, TaskScheduler.Default);
                }
            }

            public async IAsyncEnumerable<BackendEvidence> ReadEvidenceAsync([EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                yield break;
            }

            public void TerminateOwnedChild()
            {
                lock (owner._counts)
                {
                    owner.Terminations++;
                }

                if (owner.TerminateThrows)
                {
                    throw new InvalidOperationException("kill failed");
                }
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
