using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Tests.Support;

/// <summary>Real on-disk SQLite in a private temp dir, plus Business services over it.</summary>
public sealed class JobFixture : IDisposable
{
    public static readonly BoundPrincipal Operator = new("local-operator", "spike-team", "fake-agent");

    readonly TempStateDir _dir = new();

    public JobFixture(SpikeLimits? limits = null, bool testProfile = true)
    {
        Limits = limits ?? new SpikeLimits();
        TestProfile = testProfile;
        DatabasePath = _dir.File("jobs.db");
        Database = JobDatabase.Create(DatabasePath, Limits.BusyTimeout);
        Store = NewStore();
    }

    public SpikeLimits Limits { get; }

    public bool TestProfile { get; }

    public string DatabasePath { get; }

    public JobDatabase Database { get; }

    public JobStore Store { get; }

    public string? FailAt { get; set; }

    public int AcceptedSignals { get; private set; }

    public AdmissionGate Admission { get; } = new();

    public JobStore NewStore() => new(Database, new DurabilityCheckpoints(point =>
    {
        if (point == FailAt)
        {
            throw new InjectedFailureException(point);
        }
    }));

    public AcceptJob Accept(JobStore? store = null, BoundPrincipal? principal = null) =>
        new(store ?? Store, principal ?? Operator, Limits, TestProfile, Admission, () => AcceptedSignals++);

    public GetJob Get(BoundPrincipal? principal = null) => new(Store, principal ?? Operator);

    public JobView Submit(string key, string instruction = "hello", string? behavior = null, bool hold = false)
    {
        var result = Accept().Execute(new SubmitJobRequest(key, instruction, behavior, hold));
        Assert.Null(result.Error);
        return result.Job!;
    }

    public void Dispose() => _dir.Dispose();
}
