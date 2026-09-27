using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class AcceptJobTests
{
    [Fact]
    public void Store_acceptance_union_carries_jobs_only_for_success_cases()
    {
        using var f = new JobFixture(new SpikeLimits { QueueLimit = 1 });
        var request = new NewJob(JobFixture.Operator.Principal, JobFixture.Operator.Team, JobFixture.Operator.Agent,
            AcceptJob.Operation, "key", "fingerprint", "work", "options");

        if (f.Store.AcceptOrGet(request, 1) is not Accepted accepted)
        {
            throw new InvalidOperationException("expected acceptance");
        }

        Assert.Equal("key", accepted.Job.IdempotencyKey);
        if (f.Store.AcceptOrGet(request, 1) is not Existing existing)
        {
            throw new InvalidOperationException("expected existing job");
        }

        Assert.Equal(accepted.Job.JobId, existing.Job.JobId);
        Assert.True(f.Store.AcceptOrGet(request with { Fingerprint = "different" }, 1) is Conflict);
        Assert.True(f.Store.AcceptOrGet(request with { IdempotencyKey = "another" }, 1) is QueueFull);
    }

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
    public void Unnamed_real_jobs_get_a_stable_backend_name_in_the_list_projection()
    {
        using var f = new JobFixture(testProfile: false);
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, f.TestProfile, f.Admission, ["codex"]);
        var request = new SubmitJobRequest("unnamed", "work", null, false) { Backend = "codex" };
        var first = accept.Execute(request).Job!;
        var retry = accept.Execute(request).Job!;
        var name = f.List().Execute(new()).Page!.Jobs.Single().TargetAgent;

        Assert.Equal(first.JobId, retry.JobId);
        Assert.Matches("^codex-[0-9a-f]{8}$", name);
        Assert.NotEqual(JobFixture.Operator.Agent, name);
        Assert.Equal(name, f.Store.GetJob(first.JobId)!.TargetAgent);
        // The fingerprint still uses the principal's agent, so retries of jobs accepted before derived names match.
        Assert.Equal(first.JobId, accept.Execute(request with { TargetAgent = JobFixture.Operator.Agent }).Job!.JobId);

        var named = accept.Execute(request with { IdempotencyKey = "named", TargetAgent = "reviewer" }).Job!;
        Assert.Equal("reviewer", f.Store.GetJob(named.JobId)!.TargetAgent);
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

    [Theory]
    [InlineData("-model")]
    [InlineData("model'quote")]
    [InlineData("model\"quote")]
    [InlineData("model;cmd")]
    [InlineData("model&cmd")]
    [InlineData("model$HOME")]
    public void Unsafe_model_and_effort_values_are_rejected_before_acceptance(string value)
    {
        using var f = new JobFixture();
        Assert.Equal(JobErrors.InvalidRequest, f.Accept().Execute(new SubmitJobRequest("model", "x", null, false) { Model = value }).Error);
        Assert.Equal(JobErrors.InvalidRequest, f.Accept().Execute(new SubmitJobRequest("effort", "x", null, false) { Effort = value }).Error);
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
