using System.Diagnostics;
using System.Runtime.CompilerServices;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class FollowUpJobTests
{
    static ScriptedBackend Agent(string sessionId) => new(r =>
    [
        new BackendEvidence.Ack(r.Correlation),
        new BackendEvidence.Session(r.Correlation, r.ResumeSessionId ?? sessionId),
        new BackendEvidence.Result(r.Correlation, "turn done"),
    ]);

    static async Task DispatchNext(JobFixture f, BackendCatalog catalog)
    {
        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        await dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
    }

    static AcceptJob Accept(JobFixture f, BackendCatalog catalog) =>
        new(f.Store, JobFixture.Operator, f.Limits, f.TestProfile, f.Admission, catalog.Names);

    [Fact]
    public async Task Follow_up_resumes_the_parent_session_on_the_parent_backend_and_cwd()
    {
        using var f = new JobFixture();
        using var cwd = new TempStateDir();
        var claude = Agent("sess-1");
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => Agent("wrong")).Register(BackendCatalog.Claude, () => claude);
        var accept = Accept(f, catalog);

        var parent = accept.Execute(new SubmitJobRequest("p", "first", null, false) { Backend = BackendCatalog.Claude, Cwd = cwd.Path }).Job!;
        await DispatchNext(f, catalog);
        Assert.Equal("sess-1", f.Get().Execute(parent.JobId).Job!.SessionId);

        var followUp = new FollowUpJob(f.Store, JobFixture.Operator, accept);
        var child = followUp.Execute(new FollowUpRequest(parent.JobId, "second", "c"));
        Assert.Equal("accepted", child.Outcome);
        Assert.Equal("existing", followUp.Execute(new FollowUpRequest(parent.JobId, "second", "c")).Outcome);
        await DispatchNext(f, catalog);

        var resumed = claude.Started[1];
        Assert.Equal("sess-1", resumed.ResumeSessionId);
        Assert.Equal(cwd.Path, resumed.WorkingDirectory);
        var view = f.Get().Execute(child.Job!.JobId).Job!;
        Assert.Equal((JobStatus.Completed, BackendCatalog.Claude, parent.JobId, "sess-1"), (view.Status, view.Backend, view.ParentJobId, view.SessionId));
    }

    [Fact]
    public async Task Interrupt_cancels_running_turn_and_resumes_its_session()
    {
        using var f = new JobFixture();
        var running = new ScriptedBackend(r => [new BackendEvidence.Session(r.Correlation, "same-session")]) { Hangs = true };
        var parent = f.Submit("parent");
        using var dispatcher = new DispatchJob(f.Store, running, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        var attempt = dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        await Bounded.Until(() => f.Store.GetJob(parent.JobId)!.SessionId == "same-session", "session evidence");

        var followUp = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept(), dispatcher.InterruptRunning);
        Assert.Equal(JobErrors.ParentNotReady,
            followUp.Execute(new FollowUpRequest(parent.JobId, "wait", "not-interrupting")).Error);
        Assert.Equal(JobStatus.Running, f.Store.GetJob(parent.JobId)!.Status);
        Assert.Equal(0, running.Terminations);

        var child = followUp.Execute(new FollowUpRequest(parent.JobId, "new prompt", "child") { Interrupt = true });
        Assert.Equal("accepted", child.Outcome);
        await attempt.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
        var cancelled = f.Store.GetJob(parent.JobId)!;
        Assert.Equal((JobStatus.Cancelled, "interrupted"), (cancelled.Status, cancelled.ReasonCode));
        Assert.Equal("interrupted", f.Store.GetRuns(parent.JobId).Single().ReasonCode);
        Assert.Equal(1, running.Terminations);

        var resumed = Agent("ignored");
        using var next = new DispatchJob(f.Store, resumed, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        await next.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        Assert.Equal("same-session", resumed.Started.Single().ResumeSessionId);
        Assert.Equal(JobStatus.Completed, f.Store.GetJob(child.Job!.JobId)!.Status);
    }

    [Fact]
    public async Task Interrupt_racing_completion_always_accepts_the_follow_up()
    {
        using var f = new JobFixture();
        using var finish = new ManualResetEventSlim();
        IEnumerable<BackendEvidence> Script(BackendRequest request)
        {
            yield return new BackendEvidence.Session(request.Correlation, "race-session");
            finish.Wait(Bounded.ScenarioDeadline);
            yield return new BackendEvidence.Result(request.Correlation, "done");
        }

        var backend = new ScriptedBackend(Script);
        var parent = f.Submit("parent");
        using var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        var attempt = Task.Run(() => dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None), TestContext.Current.CancellationToken);
        await Bounded.Until(() => f.Store.GetJob(parent.JobId)!.SessionId == "race-session", "session evidence");
        var followUp = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept(), dispatcher.InterruptRunning);
        var pending = Task.Run(() => followUp.Execute(new FollowUpRequest(parent.JobId, "after", "child") { Interrupt = true }), TestContext.Current.CancellationToken);
        finish.Set();
        var child = await pending;
        await attempt.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
        Assert.Equal("accepted", child.Outcome);
        Assert.Contains(f.Store.GetJob(parent.JobId)!.Status, new[] { JobStatus.Completed, JobStatus.Cancelled });
        Assert.Equal("race-session", f.Store.GetJob(parent.JobId)!.SessionId);
        var resumed = Agent("unused");
        using var next = new DispatchJob(f.Store, resumed, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        await next.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        Assert.Equal("race-session", resumed.Started.Single().ResumeSessionId);
        Assert.Equal(JobStatus.Completed, f.Store.GetJob(child.Job!.JobId)!.Status);
    }

    [Fact]
    public void Follow_up_is_refused_until_the_parent_has_finished_with_a_session()
    {
        using var f = new JobFixture();
        var accept = f.Accept();
        var parent = f.Submit("p");
        var followUp = new FollowUpJob(f.Store, JobFixture.Operator, accept);

        Assert.Equal(JobErrors.ParentNotReady, followUp.Execute(new FollowUpRequest(parent.JobId, "next", "c")).Error);
        Assert.Equal(JobErrors.NotFound, followUp.Execute(new FollowUpRequest("job_missing", "next", "c")).Error);
        Assert.Equal(1, f.Store.CountUnattemptedIntents());
    }

    [Fact]
    public async Task Follow_up_is_refused_when_a_parent_with_a_session_needs_reconciliation()
    {
        using var f = new JobFixture();
        var backend = new ScriptedBackend(r =>
        [
            new BackendEvidence.Session(r.Correlation, "sess-uncertain"),
            new BackendEvidence.ProtocolError("uncertain"),
        ]);
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var accept = Accept(f, catalog);
        var parent = accept.Execute(new SubmitJobRequest("p", "first", null, false)).Job!;
        await DispatchNext(f, catalog);

        Assert.Equal(JobStatus.NeedsReconciliation, f.Store.GetJob(parent.JobId)!.Status);
        Assert.Equal(JobErrors.ParentNotReady,
            new FollowUpJob(f.Store, JobFixture.Operator, accept).Execute(new FollowUpRequest(parent.JobId, "next", "c")).Error);
        Assert.Equal(0, f.Store.CountUnattemptedIntents());
    }

    [Fact]
    public async Task Dispatch_runs_the_backend_named_by_the_job()
    {
        using var f = new JobFixture();
        var fake = Agent("f");
        var claude = Agent("c");
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => fake).Register(BackendCatalog.Claude, () => claude);

        Accept(f, catalog).Execute(new SubmitJobRequest("k", "x", null, false) { Backend = BackendCatalog.Claude });
        await DispatchNext(f, catalog);

        Assert.Single(claude.Started);
        Assert.Empty(fake.Started);
        Assert.Equal(JobErrors.BackendUnavailable,
            Accept(f, catalog).Execute(new SubmitJobRequest("k2", "x", null, false) { Backend = BackendCatalog.Codex }).Error);
    }

    [Fact]
    public async Task A_job_whose_backend_is_no_longer_configured_fails_without_starting()
    {
        using var f = new JobFixture();
        var catalog = new BackendCatalog().Register(BackendCatalog.Pi, () => Agent("p"));
        var job = Accept(f, catalog).Execute(new SubmitJobRequest("k", "x", null, false) { Backend = BackendCatalog.Pi }).Job!;

        await DispatchNext(f, new BackendCatalog());

        var stored = f.Store.GetJob(job.JobId)!;
        Assert.Equal((JobStatus.Failed, "backend_unavailable"), (stored.Status, stored.ReasonCode));
    }

    [Fact]
    public async Task Interrupt_kills_escaped_descendants_even_when_the_turn_ends_before_cancel()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var f = new JobFixture();
        var backend = new EscapingChildBackend();
        var parent = f.Submit("parent");
        using var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        var attempt = Task.Run(() => dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None), TestContext.Current.CancellationToken);
        await Bounded.Until(() => f.Store.GetJob(parent.JobId)!.SessionId == "escape-session", "session evidence");
        try
        {
            var followUp = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept(), dispatcher.InterruptRunning);
            Assert.Equal("accepted", followUp.Execute(new FollowUpRequest(parent.JobId, "next", "child") { Interrupt = true }).Outcome);
            await attempt.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);

            Assert.True(SpinWait.SpinUntil(() => !IsAlive(backend.Escaped), TimeSpan.FromSeconds(10)));
        }
        finally
        {
            try { Process.GetProcessById(backend.Escaped).Kill(); } catch (ArgumentException) { } catch (InvalidOperationException) { }
        }
    }

    static bool IsAlive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)..][0] != 'Z';
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>A marked child whose descendant left its process group; the turn ends on kill.</summary>
    sealed class EscapingChildBackend : IJobBackend
    {
        public int Escaped { get; private set; }

        public IBackendRun Start(BackendRequest request)
        {
            var info = new ProcessStartInfo("/bin/sh", ["-c", "(setsid sleep 300 & echo $!); exec sleep 300"]) { RedirectStandardOutput = true };
            OrphanedBackendProcess.Mark(info, request.Correlation);
            var process = Process.Start(info)!;
            Escaped = int.Parse(process.StandardOutput.ReadLine()!, System.Globalization.CultureInfo.InvariantCulture);
            return new Run(process, request.Correlation);
        }

        sealed class Run(Process process, string correlation) : IBackendRun
        {
            readonly ManualResetEventSlim _disposed = new();

            public int? ProcessId => process.Id;

            public Task DeliverAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public async IAsyncEnumerable<BackendEvidence> ReadEvidenceAsync([EnumeratorCancellation] CancellationToken cancellationToken)
            {
                yield return new BackendEvidence.Session(correlation, "escape-session");
                await process.WaitForExitAsync(CancellationToken.None);
                yield return new BackendEvidence.EndOfOutput();
            }

            public void TerminateOwnedChild()
            {
                process.Kill(entireProcessTree: true);
                // Hold the interrupter until the attempt has fully ended, as a slow kill would.
                _disposed.Wait(TimeSpan.FromSeconds(5));
            }

            public ValueTask DisposeAsync()
            {
                _disposed.Set();
                process.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
