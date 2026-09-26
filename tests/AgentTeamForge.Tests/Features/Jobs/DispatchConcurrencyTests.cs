using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Recovery;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

/// <summary>Bounded concurrent dispatch with per-session ordering of follow-ups.</summary>
public sealed class DispatchConcurrencyTests
{
    [Fact]
    public async Task Jobs_run_concurrently_up_to_the_configured_cap()
    {
        using var f = new JobFixture(new SpikeLimits { MaxConcurrentJobs = 3 });
        var backend = new GatedBackend();
        var jobs = Enumerable.Range(0, 5).Select(i => f.Submit($"k{i}").JobId).ToList();
        using var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        using var lifetime = new CancellationTokenSource(Bounded.ScenarioDeadline);
        var loop = dispatcher.RunAsync(lifetime.Token);

        await Bounded.Until(() => backend.Running.Count == 3, "three concurrent turns");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(3, backend.Running.Count);
        Assert.Equal(2, f.Store.CountUnattemptedIntents());

        backend.ReleaseAll();
        await Bounded.Until(() => jobs.All(id => f.Store.GetJob(id)!.Status == JobStatus.Completed), "all complete");
        Assert.Equal(3, backend.MaxRunning);
        lifetime.Cancel();
        await loop;
    }

    [Fact]
    public async Task Follow_up_waits_while_another_turn_on_its_session_runs()
    {
        using var f = new JobFixture();
        var backend = new GatedBackend();
        var accept = f.Accept();
        var parent = f.Submit("p");
        using var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        using var lifetime = new CancellationTokenSource(Bounded.ScenarioDeadline);
        var loop = dispatcher.RunAsync(lifetime.Token);
        await Bounded.Until(() => backend.Running.ContainsKey(parent.JobId), "parent running");
        backend.Release(parent.JobId);
        await Bounded.Until(() => f.Store.GetJob(parent.JobId)!.Status == JobStatus.Completed, "parent done");

        var followUp = new FollowUpJob(f.Store, JobFixture.Operator, accept);
        var first = followUp.Execute(new FollowUpRequest(parent.JobId, "second", "c1")).Job!;
        var second = followUp.Execute(new FollowUpRequest(parent.JobId, "third", "c2")).Job!;
        var unrelated = f.Submit("u").JobId;

        await Bounded.Until(() => backend.Running.ContainsKey(first.JobId) && backend.Running.ContainsKey(unrelated), "first follow-up and unrelated job run together");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(backend.Running.ContainsKey(second.JobId));
        Assert.Equal(JobStatus.Queued, f.Store.GetJob(second.JobId)!.Status);

        backend.Release(first.JobId);
        await Bounded.Until(() => backend.Running.ContainsKey(second.JobId), "second follow-up starts after the first");
        Assert.Equal(JobStatus.Completed, f.Store.GetJob(first.JobId)!.Status);
        Assert.Equal($"sess-{parent.JobId}", backend.Started[second.JobId].ResumeSessionId);

        backend.ReleaseAll();
        await Bounded.Until(() => f.Store.GetJob(second.JobId)!.Status == JobStatus.Completed, "second done");
        lifetime.Cancel();
        await loop;
    }

    [Fact]
    public async Task Shutdown_leaves_concurrent_attempts_started_and_restart_quarantines_them()
    {
        using var f = new JobFixture();
        var backend = new GatedBackend();
        var jobs = new[] { f.Submit("a").JobId, f.Submit("b").JobId };
        using var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        using var lifetime = new CancellationTokenSource(Bounded.ScenarioDeadline);
        var loop = dispatcher.RunAsync(lifetime.Token);
        await Bounded.Until(() => backend.Running.Count == 2, "both running");

        await lifetime.CancelAsync();
        await loop.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
        Assert.All(jobs, id => Assert.Equal(JobStatus.Running, f.Store.GetJob(id)!.Status));

        Assert.Equal(2, new RecoverOnStartup(f.Store).Execute().Count);
        Assert.All(jobs, id => Assert.Equal((JobStatus.NeedsReconciliation, "daemon_restart_uncertain"),
            (f.Store.GetJob(id)!.Status, f.Store.GetJob(id)!.ReasonCode)));
        Assert.Null(f.Store.BeginNextAttempt());
    }

    /// <summary>Each turn reports a session, then holds its result until released.</summary>
    sealed class GatedBackend : IJobBackend
    {
        readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();
        readonly Lock _lock = new();
        int _running;
        volatile bool _releaseAll;

        public ConcurrentDictionary<string, BackendRequest> Started { get; } = new();

        public ConcurrentDictionary<string, bool> Running { get; } = new();

        public int MaxRunning { get; private set; }

        public void Release(string jobId) => Gate(jobId).TrySetResult();

        public void ReleaseAll()
        {
            _releaseAll = true;
            foreach (var gate in _gates.Values)
            {
                gate.TrySetResult();
            }
        }

        TaskCompletionSource Gate(string jobId)
        {
            var gate = _gates.GetOrAdd(jobId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            if (_releaseAll)
            {
                gate.TrySetResult();
            }

            return gate;
        }

        public IBackendRun Start(BackendRequest request)
        {
            Started[request.JobId] = request;
            lock (_lock)
            {
                MaxRunning = Math.Max(MaxRunning, ++_running);
            }

            Running[request.JobId] = true;
            return new Run(this, request);
        }

        void Finished(string jobId)
        {
            Running.TryRemove(jobId, out _);
            lock (_lock)
            {
                _running--;
            }
        }

        sealed class Run(GatedBackend owner, BackendRequest request) : IBackendRun
        {
            public int? ProcessId => 4242;

            public Task DeliverAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public async IAsyncEnumerable<BackendEvidence> ReadEvidenceAsync([EnumeratorCancellation] CancellationToken cancellationToken)
            {
                yield return new BackendEvidence.Ack(request.Correlation);
                yield return new BackendEvidence.Session(request.Correlation, request.ResumeSessionId ?? $"sess-{request.JobId}");
                await owner.Gate(request.JobId).Task.WaitAsync(cancellationToken);
                owner.Finished(request.JobId);
                yield return new BackendEvidence.Result(request.Correlation, "done");
            }

            public void TerminateOwnedChild()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
