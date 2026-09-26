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
        Assert.Equal("not_found", endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.JobGet,
            LeadSessionId = second.SessionId,
            Workspace = second.Workspace,
            JobId = a
        }).Error);
    }

    [Fact]
    public void Restarted_lead_resumes_jobs_and_moves_unread_wake_to_new_bridge()
    {
        using var f = new JobFixture();
        var sessions = new LeadSessionStore(f.Database);
        var old = sessions.Start("/workspace/shared", "parent=old");
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

        var fresh = sessions.Start("/workspace/shared", "parent=new");
        Assert.NotEqual(old.SessionId, fresh.SessionId);
        Assert.Contains(fresh.RecoverableSessions, candidate => candidate.SessionId == old.SessionId);
        var resumed = endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.SessionResume,
            LeadSessionId = old.SessionId,
            Workspace = old.Workspace,
            BindingKey = "parent=new"
        });
        Assert.True(resumed.Ok);
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
