using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Recovery;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class SessionFenceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Unreadable_known_pid_keeps_fence_across_restart_until_exit_or_reuse_is_proven(bool reused, bool accessDenied)
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using var f = new JobFixture();
        var parent = f.Submit("a");
        var a = f.Store.BeginNextAttempt()!;
        var ar = new RunRef(a.Job.JobId, a.RunId, a.Generation, a.Correlation);
        f.Store.RecordSession(ar, "native-session");
        f.Store.Complete(ar, "done");
        var unreadable = true;
        var ownedPid = 0;
        byte[] ReadEnvironment(int pid)
        {
            // An inaccessible unrelated PID must never become a global fence.
            if (pid == Environment.ProcessId || (pid == ownedPid && unreadable))
            {
                if (accessDenied) { throw new UnauthorizedAccessException(); }
                throw new IOException("identity unavailable");
            }
            return File.ReadAllBytes($"/proc/{pid}/environ");
        }
        var follow = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept(), readProcessEnvironment: ReadEnvironment);
        var b = follow.Execute(new FollowUpRequest(parent.JobId, "second", "b")).Job!;
        var c = follow.Execute(new FollowUpRequest(parent.JobId, "third", "c")).Job!;
        var claim = f.Store.BeginNextAttempt()!;
        Assert.Equal(b.JobId, claim.Job.JobId);
        var br = new RunRef(b.JobId, claim.RunId, claim.Generation, claim.Correlation);
        var info = new System.Diagnostics.ProcessStartInfo("sleep", ["300"]) { UseShellExecute = false };
        AgentTeamForge.Business.Features.Agents.Backends.OrphanedBackendProcess.Mark(info,
            reused ? Guid.NewGuid().ToString("N") : claim.Correlation);
        using var process = System.Diagnostics.Process.Start(info)!;
        ownedPid = process.Id;
        try
        {
            f.Store.RecordBackendEvidence(br, ownedPid, false);
            f.Store.EndUnsuccessfully(br, JobStatus.NeedsReconciliation, "backend_eof");
            Assert.Equal(JobErrors.ParentNotReady, follow.Execute(new FollowUpRequest(parent.JobId, "fourth", "d")).Error);
            Assert.Null(f.Store.BeginNextAttempt());

            var restarted = f.NewStore();
            new RecoverOnStartup(restarted).Execute();
            follow = new FollowUpJob(restarted, JobFixture.Operator, f.Accept(restarted), readProcessEnvironment: ReadEnvironment);
            Assert.Equal(JobErrors.ParentNotReady, follow.Execute(new FollowUpRequest(parent.JobId, "fourth", "d")).Error);
            Assert.True(restarted.IsSessionFenced(parent.JobId));
            Assert.Null(restarted.BeginNextAttempt());
            Assert.False(process.HasExited);
            var unrelated = f.Submit("unrelated");
            Assert.Equal(unrelated.JobId, restarted.BeginNextAttempt()!.Job.JobId);
            Assert.Equal(JobStatus.Queued, restarted.GetJob(c.JobId)!.Status);

            if (reused) { unreadable = false; } // Different readable marker proves PID reuse.
            else
            {
                process.Kill();
                process.WaitForExit(); // Even a failed identity read now has independent exit proof.
            }
            Assert.Equal("accepted", follow.Execute(new FollowUpRequest(parent.JobId, "fourth", "d")).Outcome);
            Assert.False(restarted.IsSessionFenced(parent.JobId));
            Assert.Equal(c.JobId, restarted.BeginNextAttempt()!.Job.JobId);
            if (reused) { Assert.False(process.HasExited); }
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Uncertain_sibling_fences_native_session_before_its_own_session_is_recorded(bool recordSession, bool cancel)
    {
        using var f = new JobFixture();
        var parent = f.Submit("a");
        var a = f.Store.BeginNextAttempt()!;
        var ar = new RunRef(a.Job.JobId, a.RunId, a.Generation, a.Correlation);
        f.Store.RecordSession(ar, "native-session");
        f.Store.Complete(ar, "done");
        var follow = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept());
        var b = follow.Execute(new FollowUpRequest(parent.JobId, "second", "b")).Job!;
        var c = follow.Execute(new FollowUpRequest(parent.JobId, "third", "c")).Job!;
        var claim = f.Store.BeginNextAttempt()!;
        Assert.Equal(b.JobId, claim.Job.JobId);
        var br = new RunRef(b.JobId, claim.RunId, claim.Generation, claim.Correlation);
        if (recordSession) { f.Store.RecordSession(br, "native-session"); }
        if (cancel) { f.Store.Cancel(b.JobId, JobFixture.Operator.Principal, JobFixture.Operator.Team); }
        else { f.Store.EndUnsuccessfully(br, JobStatus.NeedsReconciliation, "interactive_control_failed"); }

        Assert.Null(f.Store.BeginNextAttempt());
        Assert.Equal(JobErrors.ParentNotReady, follow.Execute(new FollowUpRequest(parent.JobId, "fourth", "d")).Error);
        new RecoverOnStartup(f.NewStore()).Execute();
        Assert.Null(f.NewStore().BeginNextAttempt());
        var unrelated = f.Submit("unrelated");
        Assert.Equal(unrelated.JobId, f.Store.BeginNextAttempt()!.Job.JobId);
        Assert.Equal(JobStatus.Queued, f.Store.GetJob(c.JobId)!.Status);
        Assert.True(f.Store.TryFenceSessionForStop(parent.JobId));
        f.Store.ReconcileStoppedSession(parent.JobId);
        Assert.Equal(c.JobId, f.Store.BeginNextAttempt()!.Job.JobId);
        Assert.False(f.Store.TryFenceSessionForStop(parent.JobId));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verified_headless_sibling_exit_releases_its_fence(bool cancelled)
    {
        using var f = new JobFixture();
        var parent = f.Submit("a");
        var a = f.Store.BeginNextAttempt()!;
        var ar = new RunRef(a.Job.JobId, a.RunId, a.Generation, a.Correlation);
        f.Store.RecordSession(ar, "native-session");
        f.Store.Complete(ar, "done");
        var follow = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept());
        var b = follow.Execute(new FollowUpRequest(parent.JobId, "second", "b")).Job!;
        var claim = f.Store.BeginNextAttempt()!;
        var br = new RunRef(b.JobId, claim.RunId, claim.Generation, claim.Correlation);
        var info = new System.Diagnostics.ProcessStartInfo("true") { UseShellExecute = false };
        AgentTeamForge.Business.Features.Agents.Backends.OrphanedBackendProcess.Mark(info, claim.Correlation);
        using var process = System.Diagnostics.Process.Start(info)!;
        f.Store.RecordBackendEvidence(br, process.Id, false);
        process.WaitForExit();
        if (cancelled) { f.Store.Cancel(b.JobId, JobFixture.Operator.Principal, JobFixture.Operator.Team); }
        else { f.Store.EndUnsuccessfully(br, JobStatus.NeedsReconciliation, "backend_eof"); }
        Assert.True(f.Store.IsSessionFenced(parent.JobId));

        var c = follow.Execute(new FollowUpRequest(parent.JobId, "third", "c"));
        Assert.Equal("accepted", c.Outcome);
        Assert.False(f.Store.IsSessionFenced(parent.JobId));
        Assert.Equal(c.Job!.JobId, f.Store.BeginNextAttempt()!.Job.JobId);
    }
}
