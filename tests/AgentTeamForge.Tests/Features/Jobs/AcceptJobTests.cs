using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class AcceptJobTests
{
    [Fact]
    public void Same_key_and_meaning_resolves_to_the_same_job_with_one_intent()
    {
        using var f = new JobFixture();
        var first = f.Accept().Execute(new SubmitJobRequest("k1", "line one\nrad två ✓", null, false));
        var retry = f.Accept().Execute(new SubmitJobRequest("k1", "line one\nrad två ✓", null, false));

        Assert.Equal("accepted", first.Outcome);
        Assert.Equal("existing", retry.Outcome);
        Assert.Equal(first.Job!.JobId, retry.Job!.JobId);
        Assert.Equal(1, f.Store.CountUnattemptedIntents());
        Assert.Equal("line one\nrad två ✓", f.Store.GetJob(first.Job.JobId)!.Instruction);
    }

    [Fact]
    public void Same_key_with_different_meaning_is_a_conflict_without_side_effects()
    {
        using var f = new JobFixture();
        var original = f.Submit("k1", "one");

        var conflict = f.Accept().Execute(new SubmitJobRequest("k1", "two", null, false));
        var optionConflict = f.Accept().Execute(new SubmitJobRequest("k1", "one", null, Hold: true));

        Assert.Equal(JobErrors.IdempotencyConflict, conflict.Error);
        Assert.Equal(JobErrors.IdempotencyConflict, optionConflict.Error);
        Assert.Equal(1, f.Store.CountUnattemptedIntents());
        Assert.Equal("one", f.Store.GetJob(original.JobId)!.Instruction);
    }

    [Fact]
    public async Task Concurrent_equal_submissions_accept_exactly_one_job()
    {
        using var f = new JobFixture();
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            f.Accept(f.NewStore()).Execute(new SubmitJobRequest("race", "same", null, false)))));

        Assert.All(results, r => Assert.Null(r.Error));
        Assert.Single(results.Select(r => r.Job!.JobId).Distinct());
        Assert.Single(results, r => r.Outcome == "accepted");
        Assert.Equal(1, f.Store.CountUnattemptedIntents());
    }

    [Fact]
    public void Failed_acceptance_commit_stores_nothing_and_a_retry_can_accept()
    {
        using var f = new JobFixture();
        f.FailAt = DurabilityCheckpoints.AcceptBeforeCommit;

        Assert.Throws<InjectedFailureException>(() => f.Accept().Execute(new SubmitJobRequest("k1", "x", null, false)));
        Assert.Equal(0, f.Store.CountUnattemptedIntents());
        Assert.Equal(0, f.AcceptedSignals);

        f.FailAt = null;
        Assert.Equal("accepted", f.Accept().Execute(new SubmitJobRequest("k1", "x", null, false)).Outcome);
    }

    [Fact]
    public void Queue_admission_is_bounded_and_rejection_stores_no_key()
    {
        using var f = new JobFixture(new SpikeLimits { QueueLimit = 1 });
        f.Submit("a");

        var rejected = f.Accept().Execute(new SubmitJobRequest("b", "hello", null, false));

        Assert.Equal(JobErrors.QueueFull, rejected.Error);
        Assert.Equal(1, f.Store.CountUnattemptedIntents());
    }

    [Theory]
    [InlineData("", "x", null, false)]
    [InlineData("k", "", null, false)]
    [InlineData("k", "x", "unknown_behavior", false)]
    public void Invalid_requests_are_rejected_before_any_write(string key, string instruction, string? behavior, bool hold)
    {
        using var f = new JobFixture();
        Assert.Equal(JobErrors.InvalidRequest, f.Accept().Execute(new SubmitJobRequest(key, instruction, behavior, hold)).Error);
        Assert.Equal(JobErrors.InvalidRequest, f.Accept().Execute(new SubmitJobRequest("k", new string('x', f.Limits.MaxInstructionChars + 1), null, false)).Error);
        Assert.Equal(0, f.Store.CountUnattemptedIntents());
    }

    [Fact]
    public void Test_controls_are_refused_without_an_explicit_test_profile()
    {
        using var f = new JobFixture(testProfile: false);
        Assert.Equal(JobErrors.InvalidRequest, f.Accept().Execute(new SubmitJobRequest("k", "x", null, Hold: true)).Error);
        Assert.Equal(JobErrors.InvalidRequest, f.Accept().Execute(new SubmitJobRequest("k", "x", FakeBehavior.Hang, false)).Error);
    }

    [Fact]
    public void Another_principal_cannot_see_or_reuse_a_job()
    {
        using var f = new JobFixture();
        var job = f.Submit("k1");
        var stranger = new BoundPrincipal("someone-else", "spike-team", "fake-agent");

        Assert.Equal(JobErrors.NotFound, f.Get(stranger).Execute(job.JobId).Error);
        Assert.Equal(JobErrors.NotFound, f.Get().Execute("job_unknown").Error);
        var theirs = f.Accept(principal: stranger).Execute(new SubmitJobRequest("k1", "hello", null, false));
        Assert.NotEqual(job.JobId, theirs.Job!.JobId);
    }
}
