using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class AuthorityWiringTests
{
    static readonly Guid Machine = Guid.NewGuid();
    static readonly BoundPrincipal Connector = new("prfactory", "connector", "connector-lead");

    [Fact]
    public async Task Server_cancellation_stops_owned_queued_turn_blocks_launch_and_publishes_nothing()
    {
        using var dir = new TempStateDir();
        var db = JobDatabase.Create(dir.File("jobs.db"), TimeSpan.FromSeconds(2));
        var store = new JobStore(db, DurabilityCheckpoints.None);
        var teams = new PRFactoryTeamStore(db);
        var rows = new PRFactoryAuthorityStore(db);
        var server = new ChainServer(new PRFactoryWorkItem
        {
            Id = Guid.NewGuid(),
            RepositoryId = Guid.NewGuid(),
            ReadOnly = true,
            LeaseToken = Guid.NewGuid(),
            AgentType = PRFactoryAgentType.Codex,
            Prompt = "Do work"
        });
        var accept = new AcceptJob(store, Connector, new SpikeLimits(), true, new AdmissionGate(), ["codex"]);
        var stop = new StopJob(store, Connector, _ => { });
        var stopAgent = new StopAgent(store, Connector, new BackendCatalog().Register("codex", () => new ScriptedBackend(_ => [])));
        using var authority = new PRFactoryAuthority(ChainServer.Url, rows, teams, stop.Execute, stopAgent.Execute,
            (_, _) => true, id => store.GetJob(id)?.Status is not (JobStatus.Queued or JobStatus.Running));
        var client = server.Client();
        PRFactoryWorkItems Adapter() => new(ChainServer.Url, [new RepositoryMapping(server.Item.RepositoryId, dir.Path)], teams, client,
            accept.Execute, store.GetJob, () => { }, stopJob: stop.Execute, authority: authority);

        await Adapter().TickAsync(Machine, TestContext.Current.CancellationToken);
        var lead = Assert.Single(teams.MemberJobs(ChainServer.Url, server.Item.Id));
        Assert.Equal((ChainServer.Url, server.Item.Id), rows.OwnerOf(lead));
        Assert.True(authority.MayLaunch(server.Item.Id));
        Assert.Null(store.BeginNextAttempt(eligible: _ => false)); // A closed owner gate leaves the intent queued.
        Assert.Equal(JobStatus.Queued, store.GetJob(lead)!.Status);

        server.Status = 5; // Cancelled in the PRFactory UI.
        await Adapter().TickAsync(Machine, TestContext.Current.CancellationToken);
        Assert.False(authority.MayLaunch(server.Item.Id));
        Assert.Equal(JobStatus.Cancelled, store.GetJob(lead)!.Status);
        var row = Assert.Single(rows.Read(ChainServer.Url));
        Assert.Equal(("cancelled", false), (row.Disposition, row.Stopping));
        Assert.Equal("failed", teams.Get(ChainServer.Url, server.Item.Id)!.State);
        Assert.Empty(server.Artefacts);
        Assert.Empty(server.Completions);
        Assert.Empty(server.Failures);
    }
}
