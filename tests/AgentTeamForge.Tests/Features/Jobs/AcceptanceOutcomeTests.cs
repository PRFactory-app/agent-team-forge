using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

/// <summary>
/// A submit reply that cannot be produced after the acceptance commit must be
/// reported as an unknown outcome (never as "not accepted"), and the caller's
/// explicit same-key retry must recover the original job without a duplicate.
/// </summary>
public sealed class AcceptanceOutcomeTests
{
    [Fact]
    public void Failure_after_acceptance_commit_is_outcome_unknown_and_same_key_recovers_the_original_job()
    {
        using var f = new JobFixture();
        var endpoint = Endpoint(f);
        f.FailAt = DurabilityCheckpoints.AcceptAfterCommit;

        var lost = endpoint.Handle(Submit("k1", "x"));

        Assert.False(lost.Ok);
        Assert.Equal(IpcProtocol.OutcomeUnknown, lost.Error);
        Assert.Null(lost.Job);
        Assert.Equal(1, f.Store.CountUnattemptedIntents());
        Assert.Equal(1, f.AcceptedSignals);

        var retry = endpoint.Handle(Submit("k1", "x"));

        Assert.True(retry.Ok);
        Assert.Equal("existing", retry.Outcome);
        var stored = f.Store.GetJob(retry.Job!.JobId)!;
        Assert.Equal("k1", stored.IdempotencyKey);
        Assert.Equal(["accepted"], f.Store.GetEvents(stored.JobId).Select(e => e.Kind));
        Assert.Equal(1, f.Store.CountUnattemptedIntents());
        Assert.Equal(1, f.AcceptedSignals);
    }

    [Fact]
    public void Failure_before_acceptance_commit_is_not_reported_as_unknown_and_stores_nothing()
    {
        using var f = new JobFixture();
        var endpoint = Endpoint(f);
        f.FailAt = DurabilityCheckpoints.AcceptBeforeCommit;

        Assert.Throws<InjectedFailureException>(() => endpoint.Handle(Submit("k1", "x")));
        Assert.Equal(0, f.Store.CountUnattemptedIntents());
        Assert.Equal(0, f.AcceptedSignals);

        f.FailAt = null;
        var retry = endpoint.Handle(Submit("k1", "x"));
        Assert.Equal("accepted", retry.Outcome);
        Assert.Equal(1, f.Store.CountUnattemptedIntents());
    }

    static JobsEndpoint Endpoint(JobFixture f) =>
        new(f.Accept(), f.Get(), f.List(), new DurabilityCheckpoints(point =>
        {
            if (point == f.FailAt)
            {
                throw new InjectedFailureException(point);
            }
        }));

    static IpcRequest Submit(string key, string instruction) =>
        new() { ProtocolVersion = IpcProtocol.Version, Op = IpcProtocol.JobSubmit, IdempotencyKey = key, Instruction = instruction };
}
