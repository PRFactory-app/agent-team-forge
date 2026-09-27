using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;
using System.Diagnostics;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class DispatchJobTests
{
    [Theory]
    [InlineData(0, "completed")]
    [InlineData(7, "failed")]
    public async Task Headless_exit_reaps_detached_descendant_and_leaves_foreign_process(int exit, string expected)
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using var state = new TempStateDir();
        using var f = new JobFixture();
        using var foreign = Process.Start("/bin/sleep", ["300"])!;
        var script = state.File("fake-cursor");
        var pidFile = state.File("worker.pid");
        File.WriteAllText(script, "#!/bin/sh\n" +
            "cat >/dev/null\n" +
            "setsid sleep 300 </dev/null >/dev/null 2>&1 &\n" +
            "echo $! > '" + pidFile + "'\n" +
            (exit == 0 ? "echo '{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"done\",\"session_id\":\"s\"}'\n"
                : "echo failed >&2\n") +
            "exit " + exit + "\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var job = f.Submit("k");
        var claim = f.Store.BeginNextAttempt()!;
        try
        {
            using var dispatcher = Dispatcher(f, new CursorCliBackend(script));
            await dispatcher.RunAttemptAsync(claim, CancellationToken.None);
            Assert.Equal(expected, f.Store.GetJob(job.JobId)!.Status);
            var detached = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(SpinWait.SpinUntil(() => !Alive(detached), TimeSpan.FromSeconds(5)));
            Assert.False(foreign.HasExited);
        }
        finally
        {
            OrphanedBackendProcess.TerminateMarked([claim.Correlation]);
            if (!foreign.HasExited) { foreign.Kill(); }
        }
    }

    [Fact]
    public async Task Headless_reap_waits_for_concurrent_run_in_same_workspace()
    {
        // Cursor's worker-server is shared per project socket: the run that spawned
        // it must not kill it while another run in that workspace may be using it.
        if (!OperatingSystem.IsLinux()) { return; }
        using var state = new TempStateDir();
        using var f = new JobFixture();
        var dir = Path.GetDirectoryName(state.File("x"))!;
        var script = state.File("fake-cursor");
        File.WriteAllText(script, "#!/bin/sh\n" +
            "cat >/dev/null\n" +
            "setsid sleep 300 </dev/null >/dev/null 2>&1 &\n" +
            "if mkdir '" + dir + "/first' 2>/dev/null; then echo $! > '" + dir + "/held.pid'\n" +
            "  while [ ! -e '" + dir + "/release' ]; do sleep 0.05; done\n" +
            "else echo $! > '" + dir + "/done.pid'; fi\n" +
            "echo '{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"done\",\"session_id\":\"s\"}'\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        f.Submit("held");
        var held = f.Store.BeginNextAttempt()!;
        f.Submit("done");
        var done = f.Store.BeginNextAttempt()!;
        try
        {
            using var dispatcher = Dispatcher(f, new CursorCliBackend(script));
            var heldRun = dispatcher.RunAttemptAsync(held, CancellationToken.None);
            Assert.True(SpinWait.SpinUntil(() => File.Exists(Path.Combine(dir, "held.pid")), TimeSpan.FromSeconds(10)));
            await dispatcher.RunAttemptAsync(done, CancellationToken.None);
            var doneHelper = Pid(Path.Combine(dir, "done.pid"));
            Assert.True(Alive(doneHelper));
            File.WriteAllText(Path.Combine(dir, "release"), "");
            await heldRun;
            Assert.True(SpinWait.SpinUntil(() => !Alive(doneHelper) && !Alive(Pid(Path.Combine(dir, "held.pid"))),
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            File.WriteAllText(Path.Combine(dir, "release"), "");
            OrphanedBackendProcess.TerminateMarked([held.Correlation, done.Correlation]);
        }
    }

    static int Pid(string file) => int.Parse(File.ReadAllText(file).Trim(), System.Globalization.CultureInfo.InvariantCulture);

    static bool Alive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)..][0] != 'Z';
        }
        catch (IOException) { return false; }
    }

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
    public async Task Cli_rejection_before_a_turn_fails_without_fencing()
    {
        using var f = new JobFixture();
        var job = await DispatchOne(f, new ScriptedBackend(_ => [new BackendEvidence.NotStarted("Error: Unknown option: --mcp-config")]));

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("backend_not_started", job.ReasonCode);
        Assert.Contains("Unknown option", job.ResultText);
        Assert.False(f.Store.IsSessionFenced(job.JobId));
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

    [Fact]
    public async Task Submission_is_not_claimed_while_its_reply_is_pending_even_after_a_safety_poll()
    {
        using var f = new JobFixture();
        using var dispatcher = new DispatchJob(f.Store, new ScriptedBackend(r => [new BackendEvidence.Result(r.Correlation, "ok")]), f.Limits,
            DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        using var lifetime = new CancellationTokenSource();
        var loop = dispatcher.RunAsync(lifetime.Token);

        JobView job;
        using (dispatcher.PauseClaims())
        {
            job = f.Submit("pending-reply");
            dispatcher.Signal();
            await Task.Delay(TimeSpan.FromMilliseconds(1200), TestContext.Current.CancellationToken);
            Assert.Equal(JobStatus.Queued, f.Store.GetJob(job.JobId)!.Status);
        }

        await Bounded.Until(() => f.Store.GetJob(job.JobId)!.Status == JobStatus.Completed, "dispatch after reply");
        lifetime.Cancel();
        await loop;
    }
}
