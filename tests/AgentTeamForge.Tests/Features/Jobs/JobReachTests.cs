using System.Diagnostics;
using AgentTeamForge.Business.Features.Wake;
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
    [InlineData(null)]
    [InlineData("foreign-pid-namespace")]
    public void Invisible_bridge_without_verified_namespace_requires_explicit_resume(string? scope)
    {
        var dead = DeadBridge();
        var unverified = dead with { PidNamespace = scope };
        Assert.Null(LeadBridgeLiveness.IsLive(unverified));
        // A visible PID from another namespace cannot establish identity either.
        Assert.Null(LeadBridgeLiveness.IsLive(LeadBridgeLiveness.Current()! with { PidNamespace = scope }));
        using var f = new JobFixture();
        var workspace = Path.GetDirectoryName(f.DatabasePath)!;
        var sessions = Sessions(f);
        var old = sessions.Start(workspace, "old", "codex", "old-native", workspace, unverified);
        var job = f.Accept().Execute(new SubmitJobRequest("old-job", "hello", null, false) { LeadSessionId = old.SessionId }).Job!;
        var caller = sessions.Start(workspace, "new", "codex", "new-native", workspace, LeadBridgeLiveness.Current());
        var result = Endpoint(f, sessions).Handle(new IpcRequest
        {
            Op = IpcProtocol.JobGet,
            JobId = job.JobId,
            LeadSessionId = caller.SessionId,
            Workspace = workspace
        });
        Assert.Equal("owned_by_previous_session", result.Error);
        Assert.Equal("resume_session", result.Recovery?.Tool);
        Assert.Equal(old.SessionId, result.Recovery?.Arguments.SessionId);
        Assert.Null(result.Session);
    }

    [Theory]
    [InlineData(IpcProtocol.JobGet)]
    [InlineData(IpcProtocol.JobFollowUp)]
    [InlineData(IpcProtocol.JobStop)]
    public async Task First_call_implicitly_adopts_a_previous_native_session(string op)
    {
        using var f = new JobFixture();
        var workspace = Path.GetDirectoryName(f.DatabasePath)!;
        var sessions = Sessions(f);
        var old = sessions.Start(workspace, "old", "claude", "native-old", workspace, DeadBridge());
        var job = f.Accept().Execute(new SubmitJobRequest("old-job", "hello", null, false) { LeadSessionId = old.SessionId }).Job!;
        var backend = new ScriptedBackend(r => [new BackendEvidence.Ack(r.Correlation), new BackendEvidence.Session(r.Correlation, "child-native"),
            new BackendEvidence.Result(r.Correlation, "done")]);
        using (var dispatcher = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { }))
        {
            await dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        }
        var caller = sessions.Start(workspace, "new", "claude", "native-new", workspace, LeadBridgeLiveness.Current());
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
        var sessions = Sessions(f);
        var old = sessions.Start(workspace, "old", "claude", "native-old", workspace, DeadBridge());
        sessions.Rename(old.SessionId, workspace, "other-lead");
        if (reason == "owned_by_live_lead")
        {
            old = sessions.Start(workspace, "old", "claude", "native-old", workspace, LeadBridgeLiveness.Current());
        }
        var job = f.Accept().Execute(new SubmitJobRequest("old-job", "hello", null, false) { LeadSessionId = old.SessionId }).Job!;
        var caller = sessions.Start(workspace, "new", "claude", "native-new", reason == "owned_by_previous_session" ? workspace + "-other" : workspace);
        if (reason == "expired")
        {
            using var connection = f.Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET updated_at=$old WHERE job_id=$job";
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
        var sessions = Sessions(f);
        var old = sessions.Start(workspace, "old", "claude", "native-old", workspace, DeadBridge());
        var job = f.Accept().Execute(new SubmitJobRequest("old-job", "hello", null, false) { LeadSessionId = old.SessionId }).Job!;
        var callerWorkspace = boundary == "different-workspace" ? workspace + "-other" : workspace;
        var caller = sessions.Start(callerWorkspace, boundary == "managed-child" ? "managed-child:job" : "new",
            boundary == "unknown-native" ? null : boundary == "different-backend" ? "codex" : "claude", "new-native",
            boundary == "unknown-native" ? null : workspace);
        Assert.Equal("owned_by_previous_session", sessions.ReachJob(job.JobId, caller.SessionId, callerWorkspace, DateTimeOffset.UtcNow).Error);
    }

    [Fact]
    public void Cross_session_window_uses_last_activity_and_does_not_expire_owned_jobs()
    {
        using var f = new JobFixture();
        var workspace = Path.GetDirectoryName(f.DatabasePath)!;
        var sessions = Sessions(f);
        var lead = sessions.Start(workspace, "old", "claude", "native", workspace, DeadBridge());
        var job = f.Accept().Execute(new SubmitJobRequest("job", "hello", null, false) { LeadSessionId = lead.SessionId }).Job!;
        var caller = sessions.Start(workspace, "new", "claude", "new-native", workspace, LeadBridgeLiveness.Current());
        var now = DateTimeOffset.UtcNow;
        using var connection = f.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET accepted_at=$accepted,updated_at=$activity WHERE job_id=$job";
        command.Parameters.AddWithValue("$accepted", now.AddDays(-90).ToString("O"));
        command.Parameters.AddWithValue("$activity", now.AddDays(-30).ToString("O"));
        command.Parameters.AddWithValue("$job", job.JobId);
        command.ExecuteNonQuery();
        Assert.Null(sessions.ReachJob(job.JobId, lead.SessionId, workspace, now.AddYears(1)).Error);
        Assert.Equal("expired", sessions.ReachJob(job.JobId, caller.SessionId, workspace, now.AddTicks(1)).Error);
        Assert.Null(sessions.ReachJob(job.JobId, caller.SessionId, workspace, now).Error);
    }

    [Theory]
    [InlineData(IpcProtocol.JobStop)]
    [InlineData(IpcProtocol.JobRemoveWorktree)]
    public void Old_owned_job_still_reaches_stop_and_worktree_operations(string op)
    {
        using var f = new JobFixture();
        var workspace = Path.GetDirectoryName(f.DatabasePath)!;
        var sessions = Sessions(f);
        var lead = sessions.Start(workspace, "lead", "claude", "native", workspace);
        var job = f.Accept().Execute(new SubmitJobRequest("job", "hello", null, false) { LeadSessionId = lead.SessionId }).Job!;
        SetActivity(f, job.JobId, DateTimeOffset.UtcNow.AddDays(-90));
        var result = Endpoint(f, sessions).Handle(new IpcRequest { Op = op, JobId = job.JobId, LeadSessionId = lead.SessionId, Workspace = workspace });
        // The fixture has no worktree integration; reaching its backend-unavailable response proves access passed.
        if (op == IpcProtocol.JobStop) { Assert.True(result.Ok); }
        else { Assert.Equal(JobErrors.BackendUnavailable, result.Error); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inbox_read_never_adopts_or_reads_a_sibling(bool live)
    {
        using var f = new JobFixture();
        var workspace = Path.GetDirectoryName(f.DatabasePath)!;
        var sessions = Sessions(f);
        var old = sessions.Start(workspace, "old", "codex", "old-native", workspace,
            live ? LeadBridgeLiveness.Current() : DeadBridge());
        var job = f.Accept().Execute(new SubmitJobRequest("old-job", "hello", null, false) { LeadSessionId = old.SessionId }).Job!;
        var team = new ExternalTeam(new ExternalMemberStore(f.Database), new WakeStore(f.Database));
        var ticket = team.CreateTicket(old.SessionId, workspace, "worker", null).Ticket!;
        var token = team.Join(old.SessionId, ticket.Token).Member!.MemberToken;
        Assert.True(team.Send(token, "prior report").Ok);
        var caller = sessions.Start(workspace, "new", "codex", "new-native", workspace, LeadBridgeLiveness.Current());
        var request = new IpcRequest { Op = IpcProtocol.ExternalLeadRead, LeadSessionId = caller.SessionId, Workspace = workspace };
        var normal = Endpoint(f, sessions).Handle(request);
        Assert.True(normal.Ok);
        Assert.Empty(normal.Inbox!.Messages);
        Assert.Null(normal.Session);
        var targeted = Endpoint(f, sessions).Handle(request with { JobId = job.JobId });
        Assert.Equal(live ? "owned_by_live_lead" : "owned_by_previous_session", targeted.Error);
        Assert.Null(targeted.Inbox);
        Assert.Null(targeted.Session);
        Assert.Equal(caller.SessionId, sessions.Start(workspace, "new", "codex", "new-native", workspace).SessionId);
        Assert.Equal("prior report", Assert.Single(team.ReadLead(old.SessionId, workspace, null, null).Inbox!.Messages).Text);
    }

    [Theory]
    [InlineData("codex", "old-native")]
    [InlineData("claude", "old-native")]
    [InlineData("pi", "old-native")]
    [InlineData("codex", null)]
    [InlineData("codex", "invalid-process")]
    public void Unproven_or_null_owner_never_implicitly_adopts(string kind, string? native)
    {
        using var f = new JobFixture();
        var workspace = Path.GetDirectoryName(f.DatabasePath)!;
        var sessions = Sessions(f);
        var old = sessions.Start(workspace, "old", kind, native, workspace, native is null ? DeadBridge() : native == "invalid-process" ? new BridgeProcessIdentity(0, 0) : null);
        var job = f.Accept().Execute(new SubmitJobRequest("old-job", "hello", null, false) { LeadSessionId = old.SessionId }).Job!;
        var caller = sessions.Start(workspace, "new", kind, "new-native", workspace);
        var result = Endpoint(f, sessions).Handle(new IpcRequest
        {
            Op = IpcProtocol.JobGet,
            JobId = job.JobId,
            LeadSessionId = caller.SessionId,
            Workspace = workspace
        });
        Assert.Equal("owned_by_previous_session", result.Error);
        Assert.Equal("resume_session", result.Recovery?.Tool);
        Assert.Equal(old.SessionId, result.Recovery?.Arguments.SessionId);
        Assert.Null(result.Session);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("pi")]
    public void Real_live_bridge_blocks_even_without_a_native_wake(string kind)
    {
        using var f = new JobFixture();
        var workspace = Path.GetDirectoryName(f.DatabasePath)!;
        var sessions = Sessions(f);
        var old = sessions.Start(workspace, "old", kind, "old-native", workspace, LeadBridgeLiveness.Current());
        var job = f.Accept().Execute(new SubmitJobRequest("job", "hello", null, false) { LeadSessionId = old.SessionId }).Job!;
        var caller = sessions.Start(workspace, "new", kind, "new-native", workspace, LeadBridgeLiveness.Current());
        Assert.Equal("owned_by_live_lead", sessions.ReachJob(job.JobId, caller.SessionId, workspace, DateTimeOffset.UtcNow).Error);
        Assert.Null(sessions.Resume(old.SessionId, workspace, "new", kind, "new-native", workspace).Session);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Null_lead_jobs_are_denied_in_every_workspace(bool sameWorkspace)
    {
        using var f = new JobFixture();
        var workspace = Path.GetDirectoryName(f.DatabasePath)!;
        var sessions = Sessions(f);
        var job = f.Submit("unscoped");
        var callerWorkspace = sameWorkspace ? workspace : workspace + "-other";
        var caller = sessions.Start(callerWorkspace, "caller", "codex", "new-native", workspace, LeadBridgeLiveness.Current());
        var result = Endpoint(f, sessions).Handle(new IpcRequest
        { Op = IpcProtocol.JobGet, JobId = job.JobId, LeadSessionId = caller.SessionId, Workspace = callerWorkspace });
        Assert.Equal("owned_by_previous_session", result.Error);
        Assert.Equal("list_jobs", result.Recovery?.Tool);
        Assert.Equal("queued", f.Store.GetJob(job.JobId)!.Status);
    }

    [Fact]
    public void Caller_with_own_jobs_is_not_orphaned()
    {
        using var f = new JobFixture();
        var workspace = Path.GetDirectoryName(f.DatabasePath)!;
        var sessions = Sessions(f);
        var old = sessions.Start(workspace, "old", "codex", "old-native", workspace, DeadBridge());
        var parent = f.Accept().Execute(new SubmitJobRequest("old-job", "hello", null, false) { LeadSessionId = old.SessionId }).Job!;
        var caller = sessions.Start(workspace, "new", "codex", "new-native", workspace, LeadBridgeLiveness.Current());
        var own = f.Accept().Execute(new SubmitJobRequest("own-job", "hello", null, false) { LeadSessionId = caller.SessionId }).Job!;
        Assert.Equal("owned_by_previous_session", sessions.ReachJob(parent.JobId, caller.SessionId, workspace, DateTimeOffset.UtcNow).Error);
        Assert.Equal(caller.SessionId, sessions.Start(workspace, "new", "codex", "new-native", workspace).SessionId);
        Assert.Equal(own.JobId, Assert.Single(Endpoint(f, sessions).Handle(new IpcRequest
        { Op = IpcProtocol.JobList, LeadSessionId = caller.SessionId, Workspace = workspace }).Page!.Jobs).JobId);
    }

    [Fact]
    public async Task Competing_codex_adopters_cannot_ping_pong_a_live_binding()
    {
        using var f = new JobFixture();
        var workspace = Path.GetDirectoryName(f.DatabasePath)!;
        var sessions = Sessions(f);
        var old = sessions.Start(workspace, "old", "codex", "old-native", workspace, DeadBridge());
        var parent = f.Accept().Execute(new SubmitJobRequest("old-job", "hello", null, false) { LeadSessionId = old.SessionId }).Job!;
        var one = sessions.Start(workspace, "one", "codex", "native-one", workspace, LeadBridgeLiveness.Current());
        var two = sessions.Start(workspace, "two", "codex", "native-two", workspace, LeadBridgeLiveness.Current());
        var results = await Task.WhenAll(Task.Run(() => sessions.ReachJob(parent.JobId, one.SessionId, workspace, DateTimeOffset.UtcNow)),
            Task.Run(() => sessions.ReachJob(parent.JobId, two.SessionId, workspace, DateTimeOffset.UtcNow)));
        Assert.Single(results, r => r.Session is not null);
        Assert.Single(results, r => r.Error == "owned_by_live_lead");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Adoption_rebinds_or_parks_unread_wakes_in_the_same_transaction(bool callerHasWake)
    {
        using var f = new JobFixture();
        var workspace = Path.GetDirectoryName(f.DatabasePath)!;
        var sessions = Sessions(f);
        var wake = new WakeStore(f.Database);
        var old = sessions.Start(workspace, "old", "codex", "old-native", workspace, DeadBridge());
        var oldWake = wake.Register("codex:old", "codex", "old-native", "", workspace);
        sessions.BindWake(old.SessionId, oldWake.Key, oldWake.Generation);
        var job = f.Accept().Execute(new SubmitJobRequest("job", "hello", null, false)
        {
            LeadSessionId = old.SessionId,
            WakeKey = oldWake.Key,
            WakeGeneration = oldWake.Generation
        }).Job!;
        var claim = f.Store.BeginNextAttempt()!;
        Assert.True(f.Store.Complete(new AgentTeamForge.DAL.Features.Jobs.RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation), "done"));
        var caller = sessions.Start(workspace, "new", "codex", "new-native", workspace, LeadBridgeLiveness.Current());
        var newWake = wake.Register("codex:new", "codex", "new-native", "", workspace);
        if (callerHasWake) { sessions.BindWake(caller.SessionId, newWake.Key, newWake.Generation); }
        Assert.NotNull(sessions.ReachJob(job.JobId, caller.SessionId, workspace, DateTimeOffset.UtcNow).Session);
        Assert.Equal(callerHasWake ? newWake.Key : null, wake.Status(old.SessionId).Key);
        Assert.DoesNotContain(wake.Pending(), p => p.Target.Key == oldWake.Key);
        if (callerHasWake) { Assert.Equal(newWake.Key, Assert.Single(wake.Pending()).Target.Key); }
        else
        {
            Assert.Empty(wake.Pending());
            sessions.BindWake(old.SessionId, newWake.Key, newWake.Generation);
            Assert.Equal(newWake.Key, Assert.Single(wake.Pending()).Target.Key);
        }
    }

    static LeadSessionStore Sessions(JobFixture f) => new(f.Database)
    {
        BridgeLive = LeadBridgeLiveness.IsLive,
        OwnerLive = owner => LeadOwnerLiveness.IsLive(owner, new ClaudeWakeMailbox())
    };

    static BridgeProcessIdentity DeadBridge()
    {
        using var processes = new OwnedProcesses();
        var start = new ProcessStartInfo(SpikeRig.Binary)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("fake-backend");
        var process = processes.Start(start);
        var identity = LeadBridgeLiveness.Capture(process.Id)!;
        Assert.NotNull(identity);
        process.StandardInput.Close();
        Assert.True(process.WaitForExit(10_000));
        Assert.False(LeadBridgeLiveness.IsLive(identity));
        return identity;
    }

    static void SetActivity(JobFixture f, string job, DateTimeOffset activity)
    {
        using var connection = f.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET updated_at=$activity WHERE job_id=$job";
        command.Parameters.AddWithValue("$activity", activity.ToString("O"));
        command.Parameters.AddWithValue("$job", job);
        command.ExecuteNonQuery();
    }

    static JobsEndpoint Endpoint(JobFixture f, LeadSessionStore sessions)
    {
        var accept = f.Accept();
        return new JobsEndpoint(accept, f.Get(), new FollowUpJob(f.Store, JobFixture.Operator, accept), f.List(),
            new StopJob(f.Store, JobFixture.Operator, _ => { }), new DurabilityCheckpoints(null), () => { }, jobStore: f.Store, sessions: sessions,
            external: new ExternalTeam(new ExternalMemberStore(f.Database), new WakeStore(f.Database)));
    }
}
