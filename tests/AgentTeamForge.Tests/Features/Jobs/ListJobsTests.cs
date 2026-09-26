using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Tests.Support;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class ListJobsTests
{
    [Fact]
    public void Lists_only_jobs_of_the_bound_principal_and_team()
    {
        using var f = new JobFixture();
        var mine = f.Submit("mine");
        f.Accept(principal: new BoundPrincipal("someone-else", "spike-team", "fake-agent")).Execute(new SubmitJobRequest("x", "hello", null, false));
        f.Accept(principal: new BoundPrincipal("local-operator", "other-team", "fake-agent")).Execute(new SubmitJobRequest("y", "hello", null, false));

        var page = f.List().Execute(new ListJobsRequest()).Page!;

        Assert.Equal([mine.JobId], page.Jobs.Select(j => j.JobId));
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
        Assert.Empty(f.List(new BoundPrincipal("nobody", "spike-team", "fake-agent")).Execute(new ListJobsRequest()).Page!.Jobs);
    }

    [Fact]
    public void Pages_over_a_static_dataset_are_bounded_newest_first_and_cover_every_job_once()
    {
        using var f = new JobFixture();
        var ids = Enumerable.Range(0, 5).Select(i => f.Submit($"k{i}").JobId).ToList();

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = f.List().Execute(new ListJobsRequest(Limit: 2, Cursor: cursor)).Page!;
            Assert.True(page.Jobs.Count <= 2);
            Assert.Equal(2, page.Limit);
            Assert.Equal(page.NextCursor is not null, page.HasMore);
            seen.AddRange(page.Jobs.Select(j => j.JobId));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(ids.OrderDescending(StringComparer.Ordinal), seen);

        var defaulted = f.List().Execute(new ListJobsRequest()).Page!;
        Assert.Equal(ListJobs.DefaultPageSize, defaulted.Limit);
    }

    /// <summary>
    /// Paging is best-effort live keyset, not a snapshot: each page is its own committed read.
    /// Rows that stay matching are returned exactly once; concurrent changes may be included or missed.
    /// Same-millisecond UUIDv7 IDs do not sort in creation order, so every row whose placement
    /// relative to the cursor matters has an ID derived from one real accepted job.
    /// </summary>
    [Fact]
    public void Paging_over_concurrent_changes_never_duplicates_stable_rows()
    {
        // Nine active jobs exceed the default queue limit; admission limits are not under test here.
        using var f = new JobFixture(new SpikeLimits { QueueLimit = 16 });
        // The only job with a dispatch intent, so it is the one claim; every fixture row sorts above it.
        var leaving = f.Submit("leaving").JobId;
        var stable = Enumerable.Range(1, 5).Select(i => Offset(leaving, i * 16)).ToList();
        stable.ForEach(id => InsertQueued(f, id));
        var queued = new ListJobsRequest(Status: JobStatus.Queued, Limit: 2);

        var first = f.List().Execute(queued).Page!;
        Assert.Equal([stable[4], stable[3]], first.Jobs.Select(j => j.JobId));

        // Between pages: the lowest job leaves the filter, one job appears above the cursor and one below
        // it (as a same-millisecond UUIDv7 can), and a real acceptance lands wherever its ID sorts.
        Assert.Equal(leaving, f.Store.BeginNextAttempt()!.Job.JobId);
        var above = Offset(leaving, 1000);
        var below = Offset(stable[2], 1);
        InsertQueued(f, above);
        InsertQueued(f, below);
        var accepted = f.Submit("late").JobId;

        var seen = first.Jobs.Select(j => j.JobId).ToList();
        for (var cursor = first.NextCursor; cursor is not null;)
        {
            var page = f.List().Execute(queued with { Cursor = cursor }).Page!;
            seen.AddRange(page.Jobs.Select(j => j.JobId));
            cursor = page.NextCursor;
        }

        Assert.Equal(seen.Distinct().Count(), seen.Count);
        Assert.All(stable, id => Assert.Single(seen, id));
        Assert.DoesNotContain(leaving, seen);
        Assert.DoesNotContain(above, seen);
        Assert.Contains(below, seen);
        Assert.Equal(string.CompareOrdinal(accepted, stable[3]) < 0, seen.Contains(accepted));
    }

    [Theory]
    [InlineData(null, 0, null)]
    [InlineData(null, ListJobs.MaxPageSize + 1, null)]
    [InlineData("done", null, null)]
    [InlineData("QUEUED", null, null)]
    [InlineData(null, null, "not-a-job")]
    [InlineData(null, null, "")]
    public void Out_of_contract_requests_are_rejected(string? status, int? limit, string? cursor)
    {
        using var f = new JobFixture();
        f.Submit("k");

        var result = f.List().Execute(new ListJobsRequest(status, limit, cursor));

        Assert.Equal(JobErrors.InvalidRequest, result.Error);
        Assert.Null(result.Page);
    }

    [Fact]
    public void Reports_committed_state_and_filters_by_status()
    {
        using var f = new JobFixture();
        var completed = f.Submit("a");
        var uncertain = f.Submit("b");
        var running = f.Submit("c");
        var queued = f.Submit("d");

        // Claims take the oldest unattempted intent first.
        f.Store.Complete(Run(f.Store.BeginNextAttempt()!), "ok");
        f.Store.EndUnsuccessfully(Run(f.Store.BeginNextAttempt()!), JobStatus.NeedsReconciliation, "backend_eof");
        f.Store.BeginNextAttempt();

        var expected = new Dictionary<string, string>
        {
            [completed.JobId] = JobStatus.Completed,
            [uncertain.JobId] = JobStatus.NeedsReconciliation,
            [running.JobId] = JobStatus.Running,
            [queued.JobId] = JobStatus.Queued,
        };
        var all = f.List().Execute(new ListJobsRequest()).Page!.Jobs;
        Assert.Equal(expected.Count, all.Count);
        foreach (var job in all)
        {
            Assert.Equal(expected[job.JobId], job.Status);
            Assert.Equal(f.Store.GetJob(job.JobId)!.Attempts, job.Attempts);
        }

        var reconcile = Assert.Single(f.List().Execute(new ListJobsRequest(Status: JobStatus.NeedsReconciliation)).Page!.Jobs);
        Assert.Equal(uncertain.JobId, reconcile.JobId);
        Assert.Equal("backend_eof", reconcile.ReasonCode);
        Assert.Empty(f.List().Execute(new ListJobsRequest(Status: JobStatus.Failed)).Page!.Jobs);
    }

    [Fact]
    public void History_filters_backend_and_since_across_pages()
    {
        using var f = new JobFixture();
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => new ScriptedBackend(_ => []))
            .Register(BackendCatalog.Codex, () => new ScriptedBackend(_ => []));
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, f.TestProfile, f.Admission, catalog.Names);
        var old = accept.Execute(new SubmitJobRequest("old", "first", null, false) { Backend = BackendCatalog.Codex }).Job!;
        using (var connection = new SqliteConnection($"Data Source={f.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET accepted_at=$old WHERE job_id=$id";
            command.Parameters.AddWithValue("$old", DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
            command.Parameters.AddWithValue("$id", old.JobId);
            command.ExecuteNonQuery();
        }
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
        var recent = accept.Execute(new SubmitJobRequest("recent", "second", null, false) { Backend = BackendCatalog.Codex }).Job!;
        var fake = f.Submit("fake");

        var codex = f.List().Execute(new ListJobsRequest(Backend: BackendCatalog.Codex, Limit: 1)).Page!;
        Assert.Single(codex.Jobs);
        Assert.Equal(BackendCatalog.Codex, codex.Jobs[0].Backend);
        Assert.True(codex.HasMore);
        var next = f.List().Execute(new ListJobsRequest(Backend: BackendCatalog.Codex, Limit: 1, Cursor: codex.NextCursor)).Page!;
        Assert.Equal(new[] { old.JobId, recent.JobId }.OrderDescending(StringComparer.Ordinal),
            codex.Jobs.Concat(next.Jobs).Select(j => j.JobId));
        Assert.DoesNotContain(fake.JobId, codex.Jobs.Concat(next.Jobs).Select(j => j.JobId));
        Assert.Equal([recent.JobId], f.List().Execute(new ListJobsRequest(Backend: BackendCatalog.Codex, Since: cutoff)).Page!.Jobs.Select(j => j.JobId));
        Assert.Equal(JobErrors.InvalidRequest, f.List().Execute(new ListJobsRequest(Since: "not-a-date")).Error);
    }

    [Fact]
    public void Listing_has_no_mutation_dispatch_or_acknowledgment_effect()
    {
        using var f = new JobFixture();
        var ids = Enumerable.Range(0, 3).Select(i => f.Submit($"k{i}").JobId).ToList();
        f.Store.Complete(Run(f.Store.BeginNextAttempt()!), "ok");
        var before = Snapshot(f, ids);

        for (var i = 0; i < 3; i++)
        {
            f.List().Execute(new ListJobsRequest(Limit: 1));
            f.List().Execute(new ListJobsRequest(Status: JobStatus.Queued));
        }

        Assert.Equal(before, Snapshot(f, ids));
    }

    static void InsertQueued(JobFixture f, string jobId)
    {
        using var connection = new SqliteConnection($"Data Source={f.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs(job_id, principal, team, target_agent, operation, idempotency_key, fingerprint,
                             instruction, options, status, accepted_at, updated_at)
            VALUES ($id, $p, $t, $a, 'job_submit', $id, 'raw', 'hello', '{}', 'queued', $now, $now)
            """;
        command.Parameters.AddWithValue("$id", jobId);
        command.Parameters.AddWithValue("$p", JobFixture.Operator.Principal);
        command.Parameters.AddWithValue("$t", JobFixture.Operator.Team);
        command.Parameters.AddWithValue("$a", JobFixture.Operator.Agent);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    static string Offset(string jobId, int delta) =>
        "job_" + (UInt128.Parse(jobId[4..], System.Globalization.NumberStyles.HexNumber) + (UInt128)delta).ToString("x32");

    static RunRef Run(AttemptClaim claim) => new(claim.Job.JobId, claim.RunId, claim.Generation, claim.Correlation);

    static string Snapshot(JobFixture f, IEnumerable<string> ids) => string.Join('\n', ids.Select(id =>
        $"{f.Store.GetJob(id)}|{string.Join(',', f.Store.GetEvents(id))}|{string.Join(',', f.Store.GetRuns(id))}"))
        + $"\nintents={f.Store.CountUnattemptedIntents()}";
}
