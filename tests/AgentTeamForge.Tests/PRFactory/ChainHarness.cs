using System.Diagnostics;
using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.PRFactory;

/// <summary>
/// Real stores, authority and team workspace against a local bare remote. The test plays the
/// backend: <see cref="RunQueued"/> claims each queued connector turn and runs the supplied agent
/// action in the turn's working directory, so every Git effect is controlled and observable.
/// </summary>
sealed class ChainHarness : IDisposable
{
    public static readonly Guid Machine = Guid.Parse("0f7c3a55-1f8e-4a52-9c1c-2f7c2b9b1d11");
    static readonly BoundPrincipal Connector = new("prfactory", "connector", "connector-lead");
    readonly TempStateDir root = new();

    public ChainHarness(PRFactoryWorkItem item)
    {
        Remote = root.File("remote.git");
        Git(root.Path, "init", "--bare", "-b", "main", Remote);
        Repo = Directory.CreateDirectory(root.File("repo")).FullName;
        Git(Repo, "init", "-b", "main");
        BaseSha = Commit(Repo, "base.txt", "base");
        Git(Repo, "remote", "add", "origin", Remote);
        Git(Repo, "push", "origin", "main");
        Database = JobDatabase.Create(root.File("jobs.db"), TimeSpan.FromSeconds(2));
        Store = new JobStore(Database, DurabilityCheckpoints.None);
        Teams = new PRFactoryTeamStore(Database);
        Authorities = new PRFactoryAuthorityStore(Database);
        Workspaces = new TeamWorkspace(new PRFactoryWorkspaceStore(Database));
        Server = new ChainServer(item);
        var accept = new AcceptJob(Store, Connector, new SpikeLimits(), true, new AdmissionGate(), ["codex"]);
        Accept = accept;
        FollowUp = new FollowUpJob(Store, Connector, accept);
        Stop = new StopJob(Store, Connector, _ => { });
        var stopAgent = new StopAgent(Store, Connector, new BackendCatalog().Register("codex", () => new ScriptedBackend(_ => [])));
        Authority = new PRFactoryAuthority(ChainServer.Url, Authorities, Teams, Stop.Execute, stopAgent.Execute,
            (_, _) => true, id => Store.GetJob(id)?.Status is not (JobStatus.Queued or JobStatus.Running));
    }

    public string Remote { get; }
    public string Repo { get; }
    public string BaseSha { get; }
    public JobDatabase Database { get; }
    public JobStore Store { get; }
    public PRFactoryTeamStore Teams { get; }
    public PRFactoryAuthorityStore Authorities { get; }
    public TeamWorkspace Workspaces { get; }
    public ChainServer Server { get; }
    public AcceptJob Accept { get; }
    public FollowUpJob FollowUp { get; }
    public StopJob Stop { get; }
    public PRFactoryAuthority Authority { get; }
    public string WorkspaceRoot => root.File("workspaces");
    public AccountAdmission? Accounts { get; set; }
    public string[]? ExternalMembers { get; set; }
    public AgentTeamForge.Business.Features.External.ExternalTeam External => new(
        new AgentTeamForge.DAL.Features.External.ExternalMemberStore(Database), new AgentTeamForge.DAL.Features.Wake.WakeStore(Database));
    public HumanWaitStore HumanWaits => new(Database);
    public HumanWait HumanWait => new(HumanWaits, Store, Teams, Connector);
    public List<string> Logs { get; } = [];

    public PRFactoryWorkItems Adapter() => new(ChainServer.Url,
        [new RepositoryMapping(Server.Item.RepositoryId, Repo, ExternalMembers)], Teams, Server.Client(),
        Accept.Execute, Store.GetJob, () => { }, log: Logs.Add, externalTeam: External, stopJob: Stop.Execute, followUp: FollowUp.Execute,
        authority: Authority, workspaces: new PRFactoryWorkspace(Workspaces), workspaceRoot: WorkspaceRoot, accounts: Accounts,
        publications: new PRFactoryPublicationStore(Database),
        interaction: new PRFactoryInteraction(HumanWaits, Teams, Store, FollowUp.Execute), humanWaits: HumanWaits);

    public Task TickAsync() => Adapter().TickAsync(Machine, CancellationToken.None);

    /// <summary>Claims queued connector turns and completes each after the agent action (the "backend").</summary>
    public List<JobRecord> RunQueued(Action<JobRecord> agent)
    {
        var ran = new List<JobRecord>();
        while (Store.BeginNextAttempt() is { } claim)
        {
            var job = Store.GetJob(claim.Job.JobId)!;
            var run = new RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation);
            Store.RecordSession(run, "session-" + job.JobId);
            agent(job);
            Store.Complete(run, "done: " + job.JobId);
            ran.Add(job);
        }
        return ran;
    }

    /// <summary>Starts one queued turn and leaves it running, as a live backend would.</summary>
    public (JobRecord Job, RunRef Run) StartOne()
    {
        var claim = Store.BeginNextAttempt()!;
        var run = new RunRef(claim.Job.JobId, claim.RunId, claim.Generation, claim.Correlation);
        Store.RecordSession(run, "session-" + claim.Job.JobId);
        return (Store.GetJob(claim.Job.JobId)!, run);
    }

    public string? RemoteHead(string branch)
    {
        var output = Git(root.Path, "ls-remote", Remote, "refs/heads/" + branch);
        return output.Length == 0 ? null : output.Split('\t')[0];
    }

    public static string Commit(string repo, string file, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(repo, file))!);
        File.WriteAllText(Path.Combine(repo, file), value);
        Git(repo, "add", "--", file);
        Git(repo, "-c", "user.name=Test", "-c", "user.email=test@localhost", "commit", "-m", file);
        return Git(repo, "rev-parse", "HEAD");
    }

    public static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) { start.ArgumentList.Add(arg); }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout.Trim();
    }

    public void Dispose()
    {
        Authority.Dispose();
        Workspaces.Dispose();
        root.Dispose();
    }
}
