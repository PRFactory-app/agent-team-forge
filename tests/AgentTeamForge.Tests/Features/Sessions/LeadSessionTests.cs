using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Sessions;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Sessions;

public sealed class LeadSessionTests
{
    [Fact]
    public void Two_leads_in_one_folder_list_only_their_own_jobs_by_default()
    {
        using var f = new JobFixture();
        var sessions = new LeadSessionStore(f.Database);
        var first = sessions.Start("/workspace/shared", "parent=1");
        var second = sessions.Start("/workspace/shared", "parent=2");
        var endpoint = Endpoint(f, sessions);
        var a = Submit(endpoint, first, "same-key");
        var b = Submit(endpoint, second, "same-key");

        Assert.Equal([a], List(endpoint, first).Page!.Jobs.Select(j => j.JobId));
        Assert.Equal([b], List(endpoint, second).Page!.Jobs.Select(j => j.JobId));
        Assert.Equal(2, endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobList,
            LeadSessionId = first.SessionId,
            Workspace = first.Workspace,
            AllWorkspace = true
        }).Page!.Jobs.Count);
        // A sibling's job listed with all_workspace can be read, but not stopped unless it is fenced.
        Assert.True(endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobGet,
            LeadSessionId = second.SessionId,
            Workspace = second.Workspace,
            JobId = a
        }).Ok);
        Assert.Equal("not_found", endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobStop,
            LeadSessionId = second.SessionId,
            Workspace = second.Workspace,
            JobId = a
        }).Error);
        var other = sessions.Start("/workspace/other", "parent=3");
        Assert.Equal("not_found", endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobGet,
            LeadSessionId = other.SessionId,
            Workspace = other.Workspace,
            JobId = a
        }).Error);
    }

    [Fact]
    public void Bridge_restart_after_resume_readopts_the_resumed_session()
    {
        using var f = new JobFixture();
        var sessions = new LeadSessionStore(f.Database);
        var old = sessions.Start("/workspace/shared", "parent=old");
        Submit(Endpoint(f, sessions), old, "task");
        var fresh = sessions.Start("/workspace/shared", "parent=new");
        Assert.NotNull(sessions.Resume(old.SessionId, "/workspace/shared", "parent=new"));

        Assert.Equal(old.SessionId, sessions.Start("/workspace/shared", "parent=new").SessionId);
        Assert.NotEqual(fresh.SessionId, old.SessionId);
    }

    [Fact]
    public void Late_wake_binding_covers_jobs_still_running()
    {
        using var f = new JobFixture();
        var sessions = new LeadSessionStore(f.Database);
        var lead = sessions.Start("/workspace/shared", "parent=1");
        var id = Submit(Endpoint(f, sessions), lead, "task");
        var wake = new WakeStore(f.Database);
        var target = wake.Register("codex:late", "codex", "late", "", "/tmp");
        sessions.BindWake(lead.SessionId, target.Key, target.Generation);

        var claim = f.Store.BeginNextAttempt()!;
        Assert.Equal(id, claim.Job.JobId);
        Assert.True(f.Store.Complete(new AgentTeamForge.DAL.Features.Jobs.RunRef(claim.Job.JobId,
            claim.RunId, claim.Generation, claim.Correlation), "done"));
        Assert.Equal(target.Key, Assert.Single(wake.Pending()).Target.Key);
    }

    [Fact]
    public void Restarted_lead_resumes_jobs_and_moves_unread_wake_to_new_bridge()
    {
        using var f = new JobFixture();
        var sessions = new LeadSessionStore(f.Database);
        var workspace = Path.GetFullPath(Path.GetDirectoryName(f.DatabasePath)!);
        var old = sessions.Start(workspace, "parent=old");
        var wake = new WakeStore(f.Database);
        var oldTarget = wake.Register("codex:old", "codex", "old", "", "/tmp");
        var endpoint = Endpoint(f, sessions, wake);
        var accepted = endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobSubmit,
            LeadSessionId = old.SessionId,
            Workspace = old.Workspace,
            Backend = "fake",
            IdempotencyKey = "task",
            Instruction = "hello",
            WakeKey = oldTarget.Key,
            WakeGeneration = oldTarget.Generation
        });
        Assert.True(accepted.Ok);

        var fresh = sessions.Start(workspace, "parent=new");
        Assert.NotEqual(old.SessionId, fresh.SessionId);
        Assert.Contains(fresh.RecoverableSessions, candidate => candidate.SessionId == old.SessionId);
        var resumed = endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.SessionResume,
            LeadSessionId = old.SessionId,
            Workspace = old.Workspace,
            BindingKey = "parent=new"
        });
        Assert.True(resumed.Ok, resumed.Error);
        Assert.Equal(accepted.Job!.JobId, Assert.Single(List(endpoint, resumed.Session!).Page!.Jobs).JobId);
        var newTarget = wake.Register("codex:new", "codex", "new", "", "/tmp");
        sessions.BindWake(old.SessionId, newTarget.Key, newTarget.Generation);
        using var connection = f.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT target_key FROM wake_jobs WHERE job_id=$id";
        command.Parameters.AddWithValue("$id", accepted.Job.JobId);
        Assert.Equal(newTarget.Key, command.ExecuteScalar());
        var claim = f.Store.BeginNextAttempt()!;
        Assert.Equal(accepted.Job.JobId, claim.Job.JobId);
        Assert.True(f.Store.Complete(new AgentTeamForge.DAL.Features.Jobs.RunRef(claim.Job.JobId,
            claim.RunId, claim.Generation, claim.Correlation), "done"));
        Assert.Equal(newTarget.Key, Assert.Single(wake.Pending()).Target.Key);
    }

    [Fact]
    public void Spawned_lead_cannot_register_another_leads_valid_native_thread()
    {
        using var f = new JobFixture();
        var sessions = new LeadSessionStore(f.Database);
        var parent = sessions.Start("/workspace/shared", "parent=1");
        var endpoint = Endpoint(f, sessions, new WakeStore(f.Database));
        var firstJob = Submit(endpoint, parent, "first");
        var secondJob = Submit(endpoint, parent, "second");
        var firstThread = Guid.NewGuid().ToString("D");
        var secondThread = Guid.NewGuid().ToString("D");
        using (var connection = f.Database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE jobs SET backend='codex',session_id=$thread WHERE job_id=$job";
            command.Parameters.AddWithValue("$thread", firstThread);
            command.Parameters.AddWithValue("$job", firstJob);
            command.ExecuteNonQuery();
            command.Parameters["$thread"].Value = secondThread;
            command.Parameters["$job"].Value = secondJob;
            command.ExecuteNonQuery();
        }
        var first = sessions.Start("/workspace/shared", "managed-child:" + firstJob);
        var second = sessions.Start("/workspace/shared", "managed-child:" + secondJob);
        var request = new IpcRequest
        {
            Op = IpcProtocol.WakeRegister,
            LeadSessionId = second.SessionId,
            Workspace = second.Workspace,
            JobId = secondJob,
            WakeKey = "codex:" + firstThread,
            WakeKind = "codex",
            WakeAddress = firstThread,
            WakeHome = "/tmp"
        };
        Assert.False(endpoint.Handle(request).Ok);
        Assert.True(endpoint.Handle(request with { WakeKey = "codex:" + secondThread, WakeAddress = secondThread }).Ok);
        Assert.False(endpoint.Handle(request with { LeadSessionId = first.SessionId }).Ok);
        // A managed Claude child's socket comes from its host process, not a self-reported thread.
        Assert.True(endpoint.Handle(request with
        {
            LeadSessionId = first.SessionId,
            JobId = firstJob,
            WakeKey = "claude:/tmp/child.sock",
            WakeKind = "claude",
            WakeAddress = "/tmp/child.sock",
            WakeSecret = "token",
            WakeHome = null
        }).Ok);
    }

    [Fact]
    public void Other_folders_are_not_recoverable_or_visible()
    {
        using var f = new JobFixture();
        var sessions = new LeadSessionStore(f.Database);
        var a = sessions.Start("/workspace/a", "parent=1");
        var b = sessions.Start("/workspace/b", "parent=1");
        var endpoint = Endpoint(f, sessions);
        var id = Submit(endpoint, a, "task");
        Assert.Empty(b.RecoverableSessions);
        Assert.Empty(List(endpoint, b).Page!.Jobs);
        Assert.False(endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.SessionResume,
            LeadSessionId = a.SessionId,
            Workspace = b.Workspace,
            BindingKey = "parent=2"
        }).Ok);
        Assert.DoesNotContain(List(endpoint, b).Page!.Jobs, job => job.JobId == id);
    }

    static JobsEndpoint Endpoint(JobFixture f, LeadSessionStore sessions, WakeStore? wake = null)
    {
        var accept = f.Accept();
        return new JobsEndpoint(accept, f.Get(), new FollowUpJob(f.Store, JobFixture.Operator, accept), f.List(),
            new StopJob(f.Store, JobFixture.Operator, _ => { }), new AgentTeamForge.DAL.Sqlite.DurabilityCheckpoints(null),
            () => { }, wake, jobStore: f.Store, sessions: sessions);
    }

    static string Submit(JobsEndpoint endpoint, LeadSessionInfo session, string key)
    {
        var result = endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobSubmit,
            LeadSessionId = session.SessionId,
            Workspace = session.Workspace,
            Backend = "fake",
            IdempotencyKey = key,
            Instruction = "hello"
        });
        Assert.True(result.Ok);
        return result.Job!.JobId;
    }

    static IpcResponse List(JobsEndpoint endpoint, LeadSessionInfo session) => endpoint.Handle(new IpcRequest
    {
        Op = IpcProtocol.JobList,
        LeadSessionId = session.SessionId,
        Workspace = session.Workspace
    });
}
