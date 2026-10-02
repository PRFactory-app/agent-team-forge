using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class BulkCleanupTests
{
    static JobView Finished(JobFixture f, string key, string session, bool complete = true)
    {
        var job = f.Submit(key);
        var claim = f.Store.BeginNextAttempt()!;
        var run = new RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation);
        f.Store.RecordSession(run, session);
        if (complete) { f.Store.Complete(run, "done"); }
        return job;
    }

    static string[] Visible(JobFixture f) =>
        [.. f.Store.ListJobs("local-operator", "spike-team", null, null, null, null, 50, excludeArchived: true).Select(j => j.JobId)];

    [Fact]
    public async Task Stop_idle_closes_only_finished_sessions_proven_idle()
    {
        using var f = new JobFixture();
        var idle = Finished(f, "idle", "s-idle");
        var busy = Finished(f, "busy", "s-busy");
        var unknown = Finished(f, "unknown", "s-unknown");
        var running = Finished(f, "running", "s-running", complete: false);
        var backend = new ProbedBackend(new() { ["s-idle"] = SessionIdleState.Idle, ["s-busy"] = SessionIdleState.Busy, ["s-unknown"] = SessionIdleState.Unverified, ["s-running"] = SessionIdleState.Idle });
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var bulk = new StopIdleAgents(f.Store, JobFixture.Operator, catalog, new StopAgent(f.Store, JobFixture.Operator, catalog));

        var preview = await bulk.ExecuteAsync(dryRun: true);
        Assert.Equal((3, 0), (preview.Candidates, backend.Stopped.Count));

        var counts = await bulk.ExecuteAsync(dryRun: false);

        Assert.Equal((1, 1, 1), (counts.Stopped, counts.SkippedBusy, counts.SkippedUnverified));
        Assert.Equal(["s-idle"], backend.Stopped);
        Assert.Equal(JobStatus.Running, f.Store.GetJob(running.JobId)!.Status);
        foreach (var skipped in (JobView[])[busy, unknown]) { Assert.Equal(JobStatus.Completed, f.Store.GetJob(skipped.JobId)!.Status); }
        Assert.NotNull(idle);
    }

    [Fact]
    public void Archive_hides_finished_chains_only_and_a_follow_up_brings_the_chain_back()
    {
        using var f = new JobFixture();
        var done = Finished(f, "done", "s1");
        var failed = f.Submit("failed");
        var failedClaim = f.Store.BeginNextAttempt()!;
        Assert.True(f.Store.EndUnsuccessfully(new RunRef(failed.JobId, failedClaim.RunId, failedClaim.Generation, failedClaim.Correlation), JobStatus.Failed, "boom"));
        var reconciling = f.Submit("reconcile");
        var reconcileClaim = f.Store.BeginNextAttempt()!;
        Assert.True(f.Store.EndUnsuccessfully(new RunRef(reconciling.JobId, reconcileClaim.RunId, reconcileClaim.Generation, reconcileClaim.Correlation), JobStatus.NeedsReconciliation, "unobserved"));
        var running = Finished(f, "running", "s2", complete: false);
        // A finished parent whose follow-up is still queued is part of an unfinished chain.
        var parent = Finished(f, "parent", "s3");
        var child = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept()).Execute(new FollowUpRequest(parent.JobId, "next", "child")).Job!;
        var queued = f.Submit("queued");
        var archive = new ArchiveJobs(f.Store, JobFixture.Operator);

        Assert.Equal(2, archive.Execute(dryRun: true));
        Assert.Equal(7, Visible(f).Length);
        Assert.Equal(2, archive.Execute(dryRun: false));
        Assert.Equal(0, archive.Execute(dryRun: false));

        var visible = Visible(f);
        Assert.DoesNotContain(done.JobId, visible);
        Assert.DoesNotContain(failed.JobId, visible);
        foreach (var id in (string[])[reconciling.JobId, running.JobId, queued.JobId, parent.JobId, child.JobId]) { Assert.Contains(id, visible); }
        // MCP-style listing (no archive filter) still returns everything, flagged.
        var all = f.Store.ListJobs("local-operator", "spike-team", null, null, null, null, 50);
        Assert.Equal(7, all.Count);
        Assert.Equal([done.JobId, failed.JobId], all.Where(j => j.Archived).Select(j => j.JobId).Order());

        // Follow-up on an archived chain un-hides the whole chain.
        var revived = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept()).Execute(new FollowUpRequest(done.JobId, "again", "again")).Job!;
        Assert.Contains(done.JobId, Visible(f));
        Assert.Contains(revived.JobId, Visible(f));
        Assert.DoesNotContain(failed.JobId, Visible(f));
    }

    [Fact]
    public async Task A_follow_up_accepted_before_the_fence_is_never_stopped_or_cancelled()
    {
        using var f = new JobFixture();
        var job = Finished(f, "raced", "s-race");
        JobView? follow = null;
        var backend = new ProbedBackend(new() { ["s-race"] = SessionIdleState.Idle })
        {
            OnProbe = (_, _) => follow ??= new FollowUpJob(f.Store, JobFixture.Operator, f.Accept()).Execute(new FollowUpRequest(job.JobId, "late", "late")).Job,
        };
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var bulk = new StopIdleAgents(f.Store, JobFixture.Operator, catalog, new StopAgent(f.Store, JobFixture.Operator, catalog));

        var counts = await bulk.ExecuteAsync(dryRun: false);

        Assert.Equal(0, counts.Stopped);
        Assert.Empty(backend.Stopped);
        Assert.Equal(JobStatus.Queued, f.Store.GetJob(follow!.JobId)!.Status);
        Assert.False(f.Store.IsSessionFenced(job.JobId));
    }

    [Fact]
    public async Task A_pane_that_turns_busy_after_its_first_proof_is_not_closed_and_its_fence_is_released()
    {
        using var f = new JobFixture();
        var job = Finished(f, "flip", "s-flip");
        var backend = new ProbedBackend(new() { ["s-flip"] = SessionIdleState.Idle }) { Sequence = [SessionIdleState.Idle, SessionIdleState.Busy] };
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var bulk = new StopIdleAgents(f.Store, JobFixture.Operator, catalog, new StopAgent(f.Store, JobFixture.Operator, catalog));

        var counts = await bulk.ExecuteAsync(dryRun: false);

        Assert.Equal((0, 1), (counts.Stopped, counts.SkippedBusy));
        Assert.Empty(backend.Stopped);
        Assert.False(f.Store.IsSessionFenced(job.JobId));
        // No durable residue: a later restart fence of the same job is still releasable by recovery.
        Assert.DoesNotContain(f.Store.GetEvents(job.JobId), e => e.Kind == "stop_fenced");
        f.Store.FenceSession(job.JobId);
        Assert.True(f.Store.ReleaseRestartFence(job.JobId, "s-flip"));
        Assert.False(f.Store.IsSessionFenced(job.JobId));
    }

    [Fact]
    public async Task The_console_pass_runs_in_the_background_one_at_a_time_and_reports_progress()
    {
        using var f = new JobFixture();
        Finished(f, "bg", "s-bg");
        var backend = new ProbedBackend(new() { ["s-bg"] = SessionIdleState.Idle }) { Delay = TimeSpan.FromMilliseconds(300) };
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var bulk = new StopIdleAgents(f.Store, JobFixture.Operator, catalog, new StopAgent(f.Store, JobFixture.Operator, catalog));

        Assert.True(bulk.Start());
        Assert.False(bulk.Start());
        Assert.True(bulk.Status().Running);
        var counts = await Bounded.Until(() => Task.FromResult(bulk.Status() is { Running: false } status ? status.Counts : null), "background pass");
        Assert.Equal((1, 0), (counts.Stopped, counts.Remaining));
    }

    [Fact]
    public async Task The_final_background_counts_include_candidates_skipped_at_the_first_probe()
    {
        using var f = new JobFixture();
        Finished(f, "busy-bg", "s-busy-bg");
        var backend = new ProbedBackend(new() { ["s-busy-bg"] = SessionIdleState.Busy });
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var bulk = new StopIdleAgents(f.Store, JobFixture.Operator, catalog, new StopAgent(f.Store, JobFixture.Operator, catalog));

        Assert.True(bulk.Start());
        var counts = await Bounded.Until(() => Task.FromResult(bulk.Status() is { Running: false } status ? status.Counts : null), "background pass");

        Assert.Equal((0, 1, 0), (counts.Stopped, counts.SkippedBusy, counts.Remaining));
    }

    [Fact]
    public void A_released_bulk_reservation_keeps_no_committed_stop_marker_so_restart_recovery_can_release_the_job()
    {
        using var f = new JobFixture();
        var job = Finished(f, "failed-close", "s-fc");
        var before = f.Store.GetEvents(job.JobId).Max(e => e.Seq);
        Assert.True(f.Store.TryFenceSessionForBulkStop(job.JobId));
        // The ordinary stop path commits its marker before it touches the pane...
        Assert.True(f.Store.TryFenceSessionForStop(job.JobId));
        // ...and the close then fails: the reservation is released.
        f.Store.ReleaseBulkFence(job.JobId, before);

        Assert.False(f.Store.IsSessionFenced(job.JobId));
        Assert.DoesNotContain(f.Store.GetEvents(job.JobId), e => e.Kind == "stop_fenced");
        // Simulated restart: the surviving ownership record fences the job again, and recovery can release it.
        f.Store.FenceSession(job.JobId);
        Assert.True(f.Store.ReleaseRestartFence(job.JobId, "s-fc"));
        var follow = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept()).Execute(new FollowUpRequest(job.JobId, "again", "again"));
        Assert.Null(follow.Error);
    }

    [Fact]
    public async Task Slow_probes_stop_at_the_budget_and_report_what_remains()
    {
        using var f = new JobFixture();
        var states = new Dictionary<string, SessionIdleState>();
        for (var i = 0; i < 3; i++) { Finished(f, "slow" + i, "s-slow" + i); states["s-slow" + i] = SessionIdleState.Idle; }
        var backend = new ProbedBackend(states) { Delay = TimeSpan.FromMilliseconds(200) };
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var bulk = new StopIdleAgents(f.Store, JobFixture.Operator, catalog, new StopAgent(f.Store, JobFixture.Operator, catalog), TimeSpan.FromMilliseconds(300));

        var counts = await bulk.ExecuteAsync(dryRun: false);

        Assert.Equal((1, 2, 3), (counts.Stopped, counts.Remaining, counts.Candidates));
        Assert.Single(backend.Stopped);
    }

    sealed class ProbedBackend(Dictionary<string, SessionIdleState> states) : IJobBackend, IInteractiveSessionStop
    {
        int _calls;
        public List<string> Stopped { get; } = [];
        public Action<string, int>? OnProbe { get; init; }
        public SessionIdleState[]? Sequence { get; init; }
        public TimeSpan Delay { get; init; }
        public IBackendRun Start(BackendRequest request) => throw new InvalidOperationException("not dispatched");
        public bool HasIdleSession(string sessionId) => !Stopped.Contains(sessionId);
        public bool? HasLiveSession(string sessionId) => !Stopped.Contains(sessionId);
        public object? LaunchIdentity(string sessionId) => sessionId;
        public async Task<SessionIdleState> ProbeIdleAsync(string sessionId)
        {
            var call = _calls++;
            OnProbe?.Invoke(sessionId, call);
            if (Delay > TimeSpan.Zero) { await Task.Delay(Delay); }
            return Sequence is { } sequence ? sequence[Math.Min(call, sequence.Length - 1)] : states[sessionId];
        }
        public bool StopIdleSession(string sessionId) { Stopped.Add(sessionId); return true; }
        public void StopAllIdleSessions() { }
    }
}
