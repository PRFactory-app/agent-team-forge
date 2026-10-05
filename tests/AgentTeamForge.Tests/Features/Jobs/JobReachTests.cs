using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.External;
using AgentTeamForge.DAL.Features.External;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.DAL.Features.Sessions;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class JobReachTests
{
    [Theory]
    [InlineData(IpcProtocol.JobGet)]
    [InlineData(IpcProtocol.JobFollowUp)]
    [InlineData(IpcProtocol.JobStop)]
    [InlineData(IpcProtocol.ExternalLeadRead)]
    public async Task First_call_implicitly_adopts_a_previous_native_session(string op)
    {
        using var f = new JobFixture();
        var workspace = Path.GetDirectoryName(f.DatabasePath)!;
        var sessions = new LeadSessionStore(f.Database);
        var old = sessions.Start(workspace, "old", "claude", "native-old", workspace);
        var job = f.Accept().Execute(new SubmitJobRequest("old-job", "hello", null, false) { LeadSessionId = old.SessionId }).Job!;
        var backend = new ScriptedBackend(r => [new BackendEvidence.Ack(r.Correlation), new BackendEvidence.Session(r.Correlation, "child-native"),
            new BackendEvidence.Result(r.Correlation, "done")]);
        using (var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { }))
        {
            await dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        }
        var caller = sessions.Start(workspace, "new", "claude", "native-new", workspace);
        var endpoint = Endpoint(f, sessions);
        var result = endpoint.Handle(new IpcRequest
        {
            Op = op,
            JobId = job.JobId,
            LeadSessionId = caller.SessionId,
            Workspace = workspace,
            Instruction = "again",
            IdempotencyKey = "follow"
        });
        Assert.True(result.Ok, result.Error);
        Assert.Equal(old.SessionId, result.Session?.SessionId);

        Assert.Equal(old.SessionId, sessions.Start(workspace, "new", "claude", "native-new", workspace).SessionId);
        Assert.Equal("native-new", Assert.Single(sessions.NativeBindings([old.SessionId])).NativeId);
    }

    [Theory]
    [InlineData("unknown_job")]
    [InlineData("owned_by_live_lead")]
    [InlineData("owned_by_previous_session")]
    [InlineData("expired")]
    public void Reach_errors_have_structured_recovery_and_do_not_adopt(string reason)
    {
        using var f = new JobFixture();
        var workspace = Path.GetDirectoryName(f.DatabasePath)!;
        var sessions = new LeadSessionStore(f.Database);
        var old = sessions.Start(workspace, "old", "claude", "native-old", workspace);
        sessions.Rename(old.SessionId, workspace, "other-lead");
        sessions.OwnerLive = _ => reason == "owned_by_live_lead";
        var job = f.Accept().Execute(new SubmitJobRequest("old-job", "hello", null, false) { LeadSessionId = old.SessionId }).Job!;
        var caller = sessions.Start(workspace, "new", "claude", "native-new", reason == "owned_by_previous_session" ? workspace + "-other" : workspace);
        if (reason == "expired")
        {
            using var connection = f.Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET accepted_at=$old WHERE job_id=$job";
            command.Parameters.AddWithValue("$old", DateTimeOffset.UtcNow.AddDays(-31).ToString("O"));
            command.Parameters.AddWithValue("$job", job.JobId);
            command.ExecuteNonQuery();
        }
        var result = Endpoint(f, sessions).Handle(new IpcRequest
        {
            Op = IpcProtocol.JobGet,
            JobId = reason == "unknown_job" ? "missing" : job.JobId,
            LeadSessionId = caller.SessionId,
            Workspace = workspace
        });
        Assert.False(result.Ok);
        Assert.Equal(reason, result.Error);
        Assert.NotNull(result.Recovery);
        Assert.Null(result.Session);
        Assert.Equal("native-old", Assert.Single(sessions.NativeBindings([old.SessionId])).NativeId);
        if (reason == "owned_by_live_lead")
        {
            Assert.Equal("other-lead", result.OwnerLead);
            Assert.True(result.Recovery.Arguments.Force);
            Assert.Equal(old.SessionId, result.Recovery.Arguments.SessionId);
            Assert.NotNull(sessions.Resume(old.SessionId, workspace, "new", "claude", "native-new", workspace, force: true).Session);
        }
        if (reason == "expired") { Assert.NotNull(result.ReachExpiresAt); }
    }

    [Theory]
    [InlineData("different-workspace")]
    [InlineData("unknown-native")]
    [InlineData("managed-child")]
    [InlineData("different-backend")]
    public void Automatic_adoption_requires_a_known_owner_and_workspace(string boundary)
    {
        using var f = new JobFixture();
        var workspace = Path.GetDirectoryName(f.DatabasePath)!;
        var sessions = new LeadSessionStore(f.Database);
        var old = sessions.Start(workspace, "old", "claude", "native-old", workspace);
        var job = f.Accept().Execute(new SubmitJobRequest("old-job", "hello", null, false) { LeadSessionId = old.SessionId }).Job!;
        var callerWorkspace = boundary == "different-workspace" ? workspace + "-other" : workspace;
        var caller = sessions.Start(callerWorkspace, boundary == "managed-child" ? "managed-child:job" : "new",
            boundary == "unknown-native" ? null : boundary == "different-backend" ? "codex" : "claude", "new-native",
            boundary == "unknown-native" ? null : workspace);
        Assert.Equal("owned_by_previous_session", sessions.ReachJob(job.JobId, caller.SessionId, callerWorkspace, DateTimeOffset.UtcNow).Error);
    }

    [Fact]
    public void Thirty_day_boundary_is_inclusive_and_applies_to_current_sessions()
    {
        using var f = new JobFixture();
        var workspace = Path.GetDirectoryName(f.DatabasePath)!;
        var sessions = new LeadSessionStore(f.Database);
        var lead = sessions.Start(workspace, "lead", "claude", "native", workspace);
        var job = f.Accept().Execute(new SubmitJobRequest("job", "hello", null, false) { LeadSessionId = lead.SessionId }).Job!;
        var now = DateTimeOffset.UtcNow;
        using var connection = f.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET accepted_at=$old WHERE job_id=$job";
        command.Parameters.AddWithValue("$old", now.AddDays(-30).ToString("O"));
        command.Parameters.AddWithValue("$job", job.JobId);
        command.ExecuteNonQuery();
        Assert.Null(sessions.ReachJob(job.JobId, lead.SessionId, workspace, now).Error);
        Assert.Equal("expired", sessions.ReachJob(job.JobId, lead.SessionId, workspace, now.AddTicks(1)).Error);
    }

    [Fact]
    public void Empty_new_session_recovers_prior_inbox_without_a_job_id()
    {
        using var f = new JobFixture();
        var workspace = Path.GetDirectoryName(f.DatabasePath)!;
        var sessions = new LeadSessionStore(f.Database);
        var old = sessions.Start(workspace, "old", "claude", "old-native", workspace);
        var job = f.Accept().Execute(new SubmitJobRequest("old-job", "hello", null, false) { LeadSessionId = old.SessionId }).Job!;
        var team = new ExternalTeam(new ExternalMemberStore(f.Database), new WakeStore(f.Database));
        var ticket = team.CreateTicket(old.SessionId, workspace, "worker", null).Ticket!;
        var token = team.Join(old.SessionId, ticket.Token).Member!.MemberToken;
        Assert.True(team.Send(token, "prior report").Ok);
        var caller = sessions.Start(workspace, "new", "claude", "new-native", workspace);
        sessions.OwnerLive = _ => true;
        Assert.Null(sessions.RecoverableJob(caller.SessionId, workspace, DateTimeOffset.UtcNow));
        sessions.OwnerLive = _ => false;
        Assert.Equal(job.JobId, sessions.RecoverableJob(caller.SessionId, workspace, DateTimeOffset.UtcNow));
        var result = Endpoint(f, sessions).Handle(new IpcRequest
        {
            Op = IpcProtocol.ExternalLeadRead,
            LeadSessionId = caller.SessionId,
            Workspace = workspace
        });
        Assert.True(result.Ok);
        Assert.Equal(old.SessionId, result.Session?.SessionId);
        Assert.Equal("prior report", Assert.Single(result.Inbox!.Messages).Text);
    }

    static JobsEndpoint Endpoint(JobFixture f, LeadSessionStore sessions)
    {
        var accept = f.Accept();
        return new JobsEndpoint(accept, f.Get(), new FollowUpJob(f.Store, JobFixture.Operator, accept), f.List(),
            new StopJob(f.Store, JobFixture.Operator, _ => { }), new DurabilityCheckpoints(null), () => { }, jobStore: f.Store, sessions: sessions,
            external: new ExternalTeam(new ExternalMemberStore(f.Database), new WakeStore(f.Database)));
    }
}
