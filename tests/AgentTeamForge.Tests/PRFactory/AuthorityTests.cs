using System.Net;
using System.Diagnostics;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class AuthorityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connector_authority_cancel_closes_despite_pending_native_stop(bool codex)
    {
        using var f = await AgentTeamForge.Tests.Features.Jobs.NativeInterruptFixture.Create(codex);
        var (teams, rows, id) = Setup(f.Jobs);
        teams.RecordMember(Server, id, "lead", 0, f.Parent);
        teams.RecordMember(Server, id, "lead", 1, f.Child);
        var backend = new AgentTeamForge.Tests.Features.Jobs.NativeCloseBackend();
        f.Catalog.Register(codex ? "codex" : "claude", () => backend);
        var close = new StopAgent(f.Store, JobFixture.Operator, f.Catalog, executionStopped: f.Dispatcher.ExecutionStopped);
        f.Stop.Execute(f.Child);
        using (var authority = new PRFactoryAuthority(Server, rows, teams, close.CancelAndClose, (_, _) => true, f.Dispatcher.ExecutionStopped))
        {
            await authority.ObserveAsync(id, "cancelled", ct: TestContext.Current.CancellationToken);
            Assert.True(Assert.Single(rows.Read(Server)).Stopping);
            Assert.NotNull(f.Store.PendingNativeInterrupt(f.Child));
            Assert.NotEqual(JobStatus.Cancelled, f.Store.GetJob(f.Child)!.Status);
        }
        backend.AllowClose = true;
        using var restarted = new PRFactoryAuthority(Server, rows, teams, close.CancelAndClose, (_, _) => true, f.Dispatcher.ExecutionStopped);
        await restarted.RetryStopsAsync(TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(rows.Read(Server)).Stopping);
        Assert.Equal(f.Session, backend.Session);
        Assert.Equal(JobStatus.Cancelled, f.Store.GetJob(f.Child)!.Status);
        Assert.Null(f.Store.PendingNativeInterrupt(f.Child));
        Assert.Equal(0, f.Control.Escapes);
    }

    const string Server = "https://example.test";

    [Fact]
    public async Task Disconnected_connector_requires_fresh_confirmation_before_launch_or_effects()
    {
        using var f = new JobFixture();
        var (teams, rows, id) = Setup(f);
        using var authority = new PRFactoryAuthority(Server, rows, teams,
            _ => throw new InvalidOperationException(), (_, _) => true, _ => true);
        await authority.ObserveAsync(id, "accepted", ct: TestContext.Current.CancellationToken);
        Assert.True(authority.MayLaunch(id));
        await authority.SuspendAsync(TestContext.Current.CancellationToken);
        Assert.False(authority.MayLaunch(id));
        Assert.False(await authority.RunAsync(id, () => throw new InvalidOperationException(), TestContext.Current.CancellationToken));
        Assert.Equal("accepted", Assert.Single(rows.Read(Server)).Disposition);
        await authority.ObserveAsync(id, "accepted", ct: TestContext.Current.CancellationToken);
        Assert.True(authority.MayLaunch(id));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("revoked")]
    [InlineData("reconciliation-needed")]
    public async Task Fence_precedes_stop_and_stop_ack_waits_for_actual_quiescence(string disposition)
    {
        using var f = new JobFixture();
        var (teams, rows, id) = Setup(f);
        var job = f.Submit("owned");
        teams.RecordMember(Server, id, "lead", 0, job.JobId);
        var stopped = false;
        using var authority = new PRFactoryAuthority(Server, rows, teams, jobId =>
        {
            Assert.Equal(disposition, rows.Read(Server).Single().Disposition);
            Assert.True(rows.Read(Server).Single().Stopping);
            return new StopAgent(f.Store, JobFixture.Operator,
                new BackendCatalog().Register(BackendCatalog.Fake, () => new RetainedBackend()),
                executionStopped: _ => stopped).CancelAndClose(jobId);
        }, (_, _) => true, _ => stopped);
        await authority.ObserveAsync(id, disposition, ct: TestContext.Current.CancellationToken);
        Assert.True(rows.Read(Server).Single().Stopping);
        Assert.False(await authority.RunAsync(id, () => Task.CompletedTask, TestContext.Current.CancellationToken));
        stopped = true;
        await authority.RetryStopsAsync(TestContext.Current.CancellationToken);
        Assert.False(rows.Read(Server).Single().Stopping);
    }

    [Fact]
    public async Task Cancellation_never_signals_foreign_pid_and_keeps_stop_pending()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using var f = new JobFixture();
        var (teams, rows, id) = Setup(f);
        using var foreign = Process.Start(new ProcessStartInfo("sleep", ["30"]) { UseShellExecute = false })!;
        try
        {
            var job = f.Submit("foreign-pid");
            teams.RecordMember(Server, id, "lead", 0, job.JobId);
            var claim = f.Store.BeginNextAttempt()!;
            var run = new RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation);
            f.Store.RecordBackendEvidence(run, foreign.Id, false);
            f.Store.EndUnsuccessfully(run, JobStatus.NeedsReconciliation, "interactive_delivery_not_confirmed");
            using var dispatcher = new DispatchJob(f.Store, new ScriptedBackend(_ => []), f.Limits,
                DurabilityCheckpoints.None, f.Admission, _ => { });
            using var authority = new PRFactoryAuthority(Server, rows, teams,
                new StopAgent(f.Store, JobFixture.Operator, new BackendCatalog().Register(BackendCatalog.Fake, () => new RetainedBackend()),
                    executionStopped: _ => false).CancelAndClose,
                (_, _) => true, _ => false);
            await authority.ObserveAsync(id, "cancelled", ct: TestContext.Current.CancellationToken);
            await authority.RetryStopsAsync(TestContext.Current.CancellationToken);
            Assert.False(foreign.HasExited);
            Assert.True(rows.Read(Server).Single().Stopping);
        }
        finally { if (!foreign.HasExited) { foreign.Kill(); } }
    }

    [Fact]
    public async Task Cancel_fences_followup_and_finalize_and_stops_unmapped_deferred_turn_and_retained_session()
    {
        using var f = new JobFixture();
        var (teams, rows, id) = Setup(f);
        var parent = f.Submit("parent");
        teams.RecordMember(Server, id, "lead", 0, parent.JobId);
        var claim = f.Store.BeginNextAttempt()!;
        var run = new RunRef(parent.JobId, claim.RunId, claim.Generation, claim.Correlation);
        f.Store.RecordSession(run, "owned-session");
        var deferred = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept())
            .Execute(new(parent.JobId, "later", "child") { Defer = true }).Job!;
        f.Store.Complete(run, "done");
        var backend = new RetainedBackend();
        var stop = new StopAgent(f.Store, JobFixture.Operator, new BackendCatalog().Register(BackendCatalog.Fake, () => backend), executionStopped: _ => true);
        using var authority = new PRFactoryAuthority(Server, rows, teams,
            stop.CancelAndClose, (_, _) => true, _ => true);
        await authority.ObserveAsync(id, "accepted", ct: TestContext.Current.CancellationToken);
        await authority.ObserveAsync(id, "cancelled", ct: TestContext.Current.CancellationToken);
        Assert.False(await authority.RunAsync(id, () => throw new InvalidOperationException("late follow-up/finalize"), TestContext.Current.CancellationToken));
        Assert.Equal(JobStatus.Cancelled, f.Store.GetJob(deferred.JobId)!.Status);
        Assert.True(backend.Stopped);
        Assert.False(rows.Read(Server).Single().Stopping);
        await authority.ObserveAsync(id, "accepted", ct: TestContext.Current.CancellationToken);
        Assert.Equal("cancelled", rows.Read(Server).Single().Disposition);
    }

    [Fact]
    public async Task Inflight_effect_is_ordered_before_cancellation_and_next_effect_is_rejected()
    {
        using var f = new JobFixture();
        var (teams, rows, id) = Setup(f);
        using var authority = Authority(f, teams, rows);
        await authority.ObserveAsync(id, "accepted", ct: TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var push = authority.RunAsync(id, async () => { entered.SetResult(); await release.Task; }, TestContext.Current.CancellationToken);
        await entered.Task;
        var cancel = authority.ObserveAsync(id, "cancelled", ct: TestContext.Current.CancellationToken);
        await cancel;
        Assert.Equal("cancelled", rows.Read(Server).Single().Disposition);
        release.SetResult();
        Assert.True(await push);
        await cancel;
        Assert.False(await authority.RunAsync(id, () => Task.CompletedTask, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_waits_for_admitted_submit_and_stops_its_late_member_mapping()
    {
        using var f = new JobFixture();
        var (teams, rows, id) = Setup(f);
        using var authority = Authority(f, teams, rows);
        await authority.ObserveAsync(id, "accepted", ct: TestContext.Current.CancellationToken);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? jobId = null;
        var submit = authority.RunAsync(id, async () =>
        {
            await release.Task;
            jobId = f.Submit("late-member").JobId;
            teams.RecordMember(Server, id, "lead", 0, jobId);
        }, TestContext.Current.CancellationToken);
        await authority.ObserveAsync(id, "cancelled", ct: TestContext.Current.CancellationToken);
        Assert.True(rows.Read(Server).Single().Stopping);
        release.SetResult();
        Assert.True(await submit);
        Assert.Equal(JobStatus.Cancelled, f.Store.GetJob(jobId!)!.Status);
        Assert.False(rows.Read(Server).Single().Stopping);
    }

    [Fact]
    public async Task Offline_and_restart_retain_identity_but_require_confirmation_and_retry_failed_stops()
    {
        using var f = new JobFixture();
        var (teams, rows, id) = Setup(f);
        var job = f.Submit("owned");
        teams.RecordMember(Server, id, "lead", 0, job.JobId);
        using var authority = Authority(f, teams, rows);
        await authority.ObserveAsync(id, "accepted", ct: TestContext.Current.CancellationToken);
        await authority.TransportFailureAsync(id, HttpStatusCode.ServiceUnavailable, TestContext.Current.CancellationToken);
        Assert.Equal("accepted", rows.Read(Server).Single().Disposition);
        Assert.False(await authority.RunAsync(id, () => Task.CompletedTask, TestContext.Current.CancellationToken));
        using var restarted = new PRFactoryAuthority(Server, rows, teams,
            _ => JobResult.Fail(JobErrors.OwnershipNotProven), (_, _) => true, _ => true);
        Assert.False(await restarted.RunAsync(id, () => Task.CompletedTask, TestContext.Current.CancellationToken));
        await restarted.ObserveAsync(id, "revoked", ct: TestContext.Current.CancellationToken);
        Assert.True(rows.Read(Server).Single().Stopping); // Foreign/reused PID is never forcibly killed.
        using var retried = Authority(f, teams, rows);
        await retried.RetryStopsAsync(TestContext.Current.CancellationToken);
        Assert.False(rows.Read(Server).Single().Stopping);
        Assert.Equal("accepted", teams.Get(Server, id)!.AcceptanceState);
        Assert.NotNull(teams.Get(Server, id)!.AtfJobId);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Token_rejection_fences_all_teams_and_revokes_external_without_process_stop(HttpStatusCode status)
    {
        using var f = new JobFixture();
        var (teams, rows, id) = Setup(f);
        teams.RecordExternal(Server, id, "human", "actual-human", "external-team", "ticket", DateTimeOffset.UtcNow.AddHours(1));
        var revoked = false;
        using var authority = new PRFactoryAuthority(Server, rows, teams,
            _ => throw new InvalidOperationException("No managed process"),
            (team, member) => revoked = team == "external-team" && member == "actual-human", _ => true);
        await authority.ObserveAsync(id, "accepted", ct: TestContext.Current.CancellationToken);
        await authority.TransportFailureAsync(id, status, TestContext.Current.CancellationToken);
        Assert.True(authority.IntakeBlocked);
        Assert.True(revoked);
        Assert.True(teams.ExternalMembers(Server, id).Single().Closed);
        Assert.Equal("reconciliation-needed", rows.Read(Server).Single().Disposition);
        Assert.Equal("accepted", teams.Get(Server, id)!.AcceptanceState);
    }

    static PRFactoryAuthority Authority(JobFixture f, PRFactoryTeamStore teams, PRFactoryAuthorityStore rows) =>
        new(Server, rows, teams,
            new StopAgent(f.Store, JobFixture.Operator, new BackendCatalog().Register(BackendCatalog.Fake, () => new RetainedBackend()),
                executionStopped: _ => true).CancelAndClose, (_, _) => true, _ => true);

    static (PRFactoryTeamStore, PRFactoryAuthorityStore, Guid) Setup(JobFixture f)
    {
        var teams = new PRFactoryTeamStore(f.Database);
        var id = Guid.NewGuid();
        teams.CreateIfAbsent(Server, id, "{}", Guid.NewGuid());
        teams.SetAcceptance(Server, id, "accepted");
        return (teams, new PRFactoryAuthorityStore(f.Database), id);
    }

    sealed class RetainedBackend : IJobBackend, IInteractiveSessionStop
    {
        public bool Stopped { get; private set; }
        public IBackendRun Start(BackendRequest request) => throw new InvalidOperationException();
        public bool HasIdleSession(string sessionId) => !Stopped && sessionId == "owned-session";
        public bool StopIdleSession(string sessionId) { var owned = HasIdleSession(sessionId); Stopped |= owned; return owned; }
        public void StopAllIdleSessions() { }
    }
}
