using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class JobTimeoutTests
{
    static JobView Submit(JobFixture f, string key, int? timeout = null, int? queueTtl = null)
    {
        var result = f.Accept().Execute(new SubmitJobRequest(key, "work", null, false) { TimeoutSeconds = timeout, QueueTtlSeconds = queueTtl });
        Assert.Null(result.Error);
        return result.Job!;
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(86_401, null)]
    [InlineData(null, 0)]
    [InlineData(null, -5)]
    public void Out_of_range_limits_are_rejected(int? timeout, int? queueTtl)
    {
        using var f = new JobFixture();
        var result = f.Accept().Execute(new SubmitJobRequest("k", "work", null, false) { TimeoutSeconds = timeout, QueueTtlSeconds = queueTtl });
        Assert.Equal(JobErrors.InvalidRequest, result.Error);
    }

    [Fact]
    public async Task Running_job_past_its_timeout_is_cancelled_and_its_session_can_be_resumed()
    {
        using var f = new JobFixture();
        var backend = new ScriptedBackend(r => [new BackendEvidence.Session(r.Correlation, "session-1")]) { Hangs = true };
        var job = Submit(f, "slow", timeout: 1);
        using var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });

        await dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None).WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);

        var stored = f.Store.GetJob(job.JobId)!;
        Assert.Equal((JobStatus.Cancelled, "timeout"), (stored.Status, stored.ReasonCode));
        Assert.Equal((JobStatus.Cancelled, "timeout"), (f.Store.GetRuns(job.JobId).Single().State, f.Store.GetRuns(job.JobId).Single().ReasonCode));
        Assert.Equal(1, backend.Terminations);
        Assert.False(dispatcher.Halted);
        var next = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept()).Execute(new FollowUpRequest(job.JobId, "continue", "next"));
        Assert.Equal("accepted", next.Outcome);
    }

    [Fact]
    public async Task Queued_job_past_its_ttl_never_starts_and_is_cancelled()
    {
        using var f = new JobFixture();
        var expiring = Submit(f, "expiring", queueTtl: 1);
        var other = Submit(f, "other");
        await Task.Delay(TimeSpan.FromMilliseconds(1100), TestContext.Current.CancellationToken);

        // The claim skips it even before the sweep runs.
        Assert.Equal(other.JobId, f.Store.BeginNextAttempt()!.Job.JobId);
        Assert.Equal([expiring.JobId], f.Store.ExpireQueued());

        var stored = f.Store.GetJob(expiring.JobId)!;
        Assert.Equal((JobStatus.Cancelled, "queue_ttl"), (stored.Status, stored.ReasonCode));
        Assert.Empty(f.Store.GetRuns(expiring.JobId));
        Assert.Equal(0, f.Store.CountUnattemptedIntents());
        Assert.Empty(f.Store.ExpireQueued());
    }

    [Fact]
    public async Task Queue_ttl_starts_when_the_job_is_accepted()
    {
        using var f = new JobFixture();
        var job = new NewJob(JobFixture.Operator.Principal, JobFixture.Operator.Team, JobFixture.Operator.Agent,
            AcceptJob.Operation, "delayed", "fingerprint", "work", "behavior=complete;hold=0")
        {
            QueueTtlSeconds = 1,
        };
        await Task.Delay(TimeSpan.FromMilliseconds(1100), TestContext.Current.CancellationToken);

        var accepted = f.Store.AcceptOrGet(job, f.Limits.QueueLimit);
        if (accepted is not Accepted acceptedCase)
        {
            throw new InvalidOperationException("expected acceptance");
        }
        Assert.Equal(acceptedCase.Job.JobId, f.Store.BeginNextAttempt()!.Job.JobId);
    }

    [Fact]
    public async Task Sweep_cannot_cancel_a_job_that_was_claimed()
    {
        using var f = new JobFixture();
        var job = Submit(f, "claimed", queueTtl: 1);
        var claim = f.Store.BeginNextAttempt()!;
        await Task.Delay(TimeSpan.FromMilliseconds(1100), TestContext.Current.CancellationToken);

        Assert.Empty(f.Store.ExpireQueued());
        Assert.Equal(JobStatus.Running, f.Store.GetJob(job.JobId)!.Status);
        Assert.True(f.Store.Complete(new RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation), "done"));
        Assert.Equal(JobStatus.Completed, f.Store.GetJob(job.JobId)!.Status);
    }

    [Fact]
    public void Completion_and_stop_keep_the_first_terminal_result_when_timeout_fires()
    {
        using var f = new JobFixture();
        var completed = Submit(f, "completed", timeout: 1);
        var completionClaim = f.Store.BeginNextAttempt()!;
        Assert.True(f.Store.Complete(new RunRef(completed.JobId, completionClaim.RunId, completionClaim.Generation, completionClaim.Correlation), "done"));
        Assert.False(f.Store.CancelOwned(completed.JobId, "timeout").Changed);
        Assert.Equal((JobStatus.Completed, "done"), (f.Store.GetJob(completed.JobId)!.Status, f.Store.GetJob(completed.JobId)!.ResultText));

        var stopped = Submit(f, "stopped", timeout: 1);
        var stopClaim = f.Store.BeginNextAttempt()!;
        Assert.Equal(stopped.JobId, stopClaim.Job.JobId);
        Assert.True(f.Store.Cancel(stopped.JobId, JobFixture.Operator.Principal, JobFixture.Operator.Team).Changed);
        Assert.False(f.Store.CancelOwned(stopped.JobId, "timeout").Changed);
        Assert.Equal((JobStatus.Cancelled, "stopped"), (f.Store.GetJob(stopped.JobId)!.Status, f.Store.GetJob(stopped.JobId)!.ReasonCode));
        Assert.Single(f.Store.GetEvents(stopped.JobId), e => e.Kind == JobStatus.Cancelled);
    }

    [Fact]
    public void Limits_are_part_of_the_idempotent_request()
    {
        using var f = new JobFixture();
        Submit(f, "same", timeout: 30);
        Assert.Equal("existing", f.Accept().Execute(new SubmitJobRequest("same", "work", null, false) { TimeoutSeconds = 30 }).Outcome);
        Assert.Equal(JobErrors.IdempotencyConflict, f.Accept().Execute(new SubmitJobRequest("same", "work", null, false) { TimeoutSeconds = 60 }).Error);

        Submit(f, "ttl", queueTtl: 30);
        Assert.Equal("existing", f.Accept().Execute(new SubmitJobRequest("ttl", "work", null, false) { QueueTtlSeconds = 30 }).Outcome);
        Assert.Equal(JobErrors.IdempotencyConflict, f.Accept().Execute(new SubmitJobRequest("ttl", "work", null, false) { QueueTtlSeconds = 60 }).Error);

        using var followFixture = new JobFixture();
        var parent = Submit(followFixture, "parent");
        var claim = followFixture.Store.BeginNextAttempt()!;
        Assert.Equal(parent.JobId, claim.Job.JobId);
        var run = new RunRef(parent.JobId, claim.RunId, claim.Generation, claim.Correlation);
        Assert.True(followFixture.Store.RecordSession(run, "session-1"));
        Assert.True(followFixture.Store.Complete(run, "done"));
        var followUp = new FollowUpJob(followFixture.Store, JobFixture.Operator, followFixture.Accept());
        Assert.Equal("accepted", followUp.Execute(new FollowUpRequest(parent.JobId, "continue", "follow") { TimeoutSeconds = 30, QueueTtlSeconds = 30 }).Outcome);
        Assert.Equal("existing", followUp.Execute(new FollowUpRequest(parent.JobId, "continue", "follow") { TimeoutSeconds = 30, QueueTtlSeconds = 30 }).Outcome);
        Assert.Equal(JobErrors.IdempotencyConflict, followUp.Execute(new FollowUpRequest(parent.JobId, "continue", "follow") { TimeoutSeconds = 30, QueueTtlSeconds = 60 }).Error);
    }

    [Fact]
    public async Task Timed_out_claude_turn_keeps_its_upfront_session_id()
    {
        using var f = new JobFixture();
        using var dir = new TempStateDir();
        var script = dir.File("claude");
        File.WriteAllText(script, $"#!/usr/bin/env bash\nprintf '%s\\n' \"$@\" > '{dir.File("argv")}'\ncat >/dev/null\nexec sleep 60\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var job = Submit(f, "claude-slow", timeout: 1);
        using var dispatcher = new DispatchJob(f.Store, new ClaudeCodeBackend(script), f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });

        await dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None).WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);

        var stored = f.Store.GetJob(job.JobId)!;
        Assert.Equal((JobStatus.Cancelled, "timeout"), (stored.Status, stored.ReasonCode));
        Assert.Equal(File.ReadAllLines(dir.File("argv"))[^1], stored.SessionId);
        var pid = f.Store.GetRuns(job.JobId).Single().BackendPid!.Value;
        await Bounded.Until(() => !Directory.Exists($"/proc/{pid}"), "claude process killed");
    }
}
