using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class DispatchJobTests
{
    static DispatchJob Dispatcher(JobFixture f, IJobBackend backend) =>
        new(f.Store, backend, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });

    static async Task<JobRecord> DispatchOne(JobFixture f, IJobBackend backend, DispatchJob? dispatcher = null)
    {
        var job = f.Submit("k", "do it");
        var claim = f.Store.BeginNextAttempt()!;
        await (dispatcher ?? Dispatcher(f, backend)).RunAttemptAsync(claim, CancellationToken.None);
        return f.Store.GetJob(job.JobId)!;
    }

    [Fact]
    public async Task Attempt_start_is_committed_before_the_backend_is_touched()
    {
        using var f = new JobFixture();
        RunRecord? seenAtStart = null;
        var backend = new ScriptedBackend(r => [new BackendEvidence.Result(r.Correlation, "ok")])
        {
            OnStart = r => seenAtStart = f.NewStore().GetRuns(r.JobId).Single(),
        };

        await DispatchOne(f, backend);

        Assert.NotNull(seenAtStart);
        Assert.Equal("started", seenAtStart.State);
        Assert.Equal(backend.Started.Single().Correlation, seenAtStart.Correlation);
    }

    [Fact]
    public async Task Correlated_result_completes_job_result_and_event_together()
    {
        using var f = new JobFixture();
        var backend = new ScriptedBackend(r => [new BackendEvidence.Ack(r.Correlation), new BackendEvidence.Result(r.Correlation, "done ✓")]);

        var job = await DispatchOne(f, backend);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal("done ✓", job.ResultText);
        Assert.Equal(["accepted", "attempt_started", "completed"], f.Store.GetEvents(job.JobId).Select(e => e.Kind));
        Assert.True(f.Store.GetRuns(job.JobId).Single().Acked);
    }

    [Theory]
    [InlineData("ack_then_eof", "backend_eof")]
    [InlineData("mismatched_then_eof", "backend_eof")]
    [InlineData("eof_only", "backend_eof")]
    [InlineData("malformed", "backend_malformed_output")]
    public async Task Non_authoritative_evidence_never_completes_and_is_never_requeued(string script, string reason)
    {
        using var f = new JobFixture();
        var backend = new ScriptedBackend(r => script switch
        {
            "ack_then_eof" => [new BackendEvidence.Ack(r.Correlation), new BackendEvidence.EndOfOutput()],
            "mismatched_then_eof" => [new BackendEvidence.Result("stale", "nope"), new BackendEvidence.EndOfOutput()],
            "eof_only" => [new BackendEvidence.EndOfOutput()],
            _ => [new BackendEvidence.ProtocolError("backend_malformed_output")],
        });

        var job = await DispatchOne(f, backend);

        Assert.Equal(JobStatus.NeedsReconciliation, job.Status);
        Assert.Equal(reason, job.ReasonCode);
        Assert.Null(job.ResultText);
        Assert.Equal(0, f.Store.CountUnattemptedIntents());
        Assert.Null(f.Store.BeginNextAttempt());
    }

    [Fact]
    public async Task Deadline_kills_only_the_owned_child_and_quarantines()
    {
        using var f = new JobFixture(new SpikeLimits { MaxFakeRuntime = TimeSpan.FromMilliseconds(200) });
        var backend = new ScriptedBackend(r => [new BackendEvidence.Ack(r.Correlation)]) { Hangs = true };

        var job = await DispatchOne(f, backend);

        Assert.Equal(JobStatus.NeedsReconciliation, job.Status);
        Assert.Equal("backend_timeout", job.ReasonCode);
        Assert.Equal(1, backend.Terminations);
    }

    [Fact]
    public async Task Backend_that_provably_never_started_fails_without_requeue()
    {
        using var f = new JobFixture();
        var job = await DispatchOne(f, new ScriptedBackend(_ => []) { NeverStarts = true });

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("backend_not_started", job.ReasonCode);
        Assert.Null(f.Store.BeginNextAttempt());
    }

    [Fact]
    public async Task Failed_completion_write_leaves_no_partial_completion_and_halts_dispatch()
    {
        using var f = new JobFixture();
        f.FailAt = DurabilityCheckpoints.CompleteBeforeCommit;
        var backend = new ScriptedBackend(r => [new BackendEvidence.Result(r.Correlation, "ok")]);
        using var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });

        var job = await DispatchOne(f, backend, dispatcher);

        Assert.Equal(JobStatus.NeedsReconciliation, job.Status);
        Assert.Equal("completion_write_failed", job.ReasonCode);
        Assert.Null(job.ResultText);
        Assert.DoesNotContain(f.Store.GetEvents(job.JobId), e => e.Kind == "completed");
        Assert.True(dispatcher.Halted);
    }

    [Fact]
    public void Stale_generation_or_correlation_cannot_complete_the_current_run()
    {
        using var f = new JobFixture();
        var job = f.Submit("k");
        var claim = f.Store.BeginNextAttempt()!;

        Assert.False(f.Store.Complete(new RunRef(job.JobId, claim.RunId, claim.Generation, "other"), "x"));
        Assert.False(f.Store.Complete(new RunRef(job.JobId, claim.RunId, claim.Generation + 1, claim.Correlation), "x"));
        Assert.True(f.Store.Complete(new RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation), "x"));
        Assert.False(f.Store.Complete(new RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation), "again"));
        Assert.Equal("x", f.Store.GetJob(job.JobId)!.ResultText);
    }

    [Fact]
    public async Task Queued_work_dispatches_from_the_loop_without_any_client()
    {
        using var f = new JobFixture();
        using var dispatcher = new DispatchJob(f.Store, new ScriptedBackend(r => [new BackendEvidence.Result(r.Correlation, "ok")]), f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        var job = f.Submit("k");
        using var lifetime = new CancellationTokenSource();
        var loop = dispatcher.RunAsync(lifetime.Token);

        await Bounded.Until(() => f.Store.GetJob(job.JobId)!.Status == JobStatus.Completed, "dispatch");
        lifetime.Cancel();
        await loop;
    }
}
