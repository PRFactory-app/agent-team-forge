using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class StopJobTests
{
    [Fact]
    public void Queued_job_is_cancelled_without_an_attempt()
    {
        using var f = new JobFixture();
        var job = f.Submit("queued");
        var stop = new StopJob(f.Store, JobFixture.Operator, _ => throw new InvalidOperationException("no running job"));

        Assert.Equal(JobStatus.Cancelled, stop.Execute(job.JobId).Job!.Status);
        Assert.Null(f.Store.BeginNextAttempt());
        Assert.Empty(f.Store.GetRuns(job.JobId));
        Assert.Equal(0, f.Store.CountUnattemptedIntents());
        Assert.Equal(JobStatus.Cancelled, new JobStore(JobDatabase.Open(f.DatabasePath, f.Limits.BusyTimeout), DurabilityCheckpoints.None).GetJob(job.JobId)!.Status);
    }

    [Fact]
    public async Task Running_job_terminates_owned_backend_and_can_resume_its_session()
    {
        using var f = new JobFixture();
        var backend = new ScriptedBackend(r =>
        [
            new BackendEvidence.Ack(r.Correlation),
            new BackendEvidence.Session(r.Correlation, "session-1"),
        ])
        { Hangs = true };
        var job = f.Submit("running");
        using var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        var attempt = dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        await Bounded.Until(() => f.Store.GetJob(job.JobId)!.SessionId == "session-1", "session evidence");

        var stopped = new StopJob(f.Store, JobFixture.Operator, dispatcher.CancelRunning).Execute(job.JobId);
        await attempt.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
        Assert.Equal(JobStatus.Cancelled, stopped.Job!.Status);
        Assert.Equal(1, backend.Terminations);
        Assert.Equal(JobStatus.Cancelled, f.Store.GetRuns(job.JobId).Single().State);
        Assert.Equal(JobStatus.Cancelled, new JobStore(JobDatabase.Open(f.DatabasePath, f.Limits.BusyTimeout), DurabilityCheckpoints.None).GetJob(job.JobId)!.Status);

        var followUp = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept());
        var next = followUp.Execute(new FollowUpRequest(job.JobId, "continue", "next"));
        Assert.Equal("accepted", next.Outcome);
        var resumed = new ScriptedBackend(r =>
        [
            new BackendEvidence.Result(r.Correlation, "done"),
        ]);
        using var nextDispatcher = new DispatchJob(f.Store, resumed, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        await nextDispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        Assert.Equal("session-1", resumed.Started.Single().ResumeSessionId);
        Assert.Equal(JobStatus.Completed, f.Store.GetJob(next.Job!.JobId)!.Status);
    }

    [Fact]
    public async Task Repeated_stop_and_stop_of_completed_job_return_current_state()
    {
        using var f = new JobFixture();
        var stop = new StopJob(f.Store, JobFixture.Operator, _ => { });
        var cancelled = f.Submit("cancelled");
        stop.Execute(cancelled.JobId);
        var repeated = stop.Execute(cancelled.JobId);
        Assert.Equal((JobStatus.Cancelled, "unchanged"), (repeated.Job!.Status, repeated.Outcome));
        Assert.Single(f.Store.GetEvents(cancelled.JobId), e => e.Kind == JobStatus.Cancelled);

        var completed = f.Submit("completed");
        using var dispatcher = new DispatchJob(f.Store,
            new ScriptedBackend(r => [new BackendEvidence.Result(r.Correlation, "done")]),
            f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        await dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        var terminal = stop.Execute(completed.JobId);
        Assert.Equal((JobStatus.Completed, "unchanged"), (terminal.Job!.Status, terminal.Outcome));
        Assert.Equal("done", f.Store.GetJob(completed.JobId)!.ResultText);
    }

    [Fact]
    public async Task Result_racing_a_committed_stop_is_fenced_out_without_halting_dispatch()
    {
        using var f = new JobFixture();
        using var resultReady = new ManualResetEventSlim();
        IEnumerable<BackendEvidence> Script(BackendRequest r)
        {
            yield return new BackendEvidence.Session(r.Correlation, "session-1");
            resultReady.Wait(Bounded.ScenarioDeadline);
            yield return new BackendEvidence.Result(r.Correlation, "late");
        }

        var backend = new ScriptedBackend(Script);
        var job = f.Submit("race");
        using var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        var claim = f.Store.BeginNextAttempt()!;
        var attempt = Task.Run(() => dispatcher.RunAttemptAsync(claim, CancellationToken.None), TestContext.Current.CancellationToken);
        await Bounded.Until(() => f.Store.GetJob(job.JobId)!.SessionId == "session-1", "session evidence");

        new StopJob(f.Store, JobFixture.Operator, dispatcher.CancelRunning).Execute(job.JobId);
        resultReady.Set();
        await attempt.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);

        var stored = f.Store.GetJob(job.JobId)!;
        Assert.Equal((JobStatus.Cancelled, null), (stored.Status, stored.ResultText));
        Assert.False(dispatcher.Halted);
        Assert.Equal(1, backend.Terminations);
        // A crash before the kill completed must still find this run's processes on restart.
        Assert.Contains(claim.Correlation, f.Store.GetInterruptedRunCorrelations());
    }
}
