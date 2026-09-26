using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.Tests.Support;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class PruneJobTests
{
    [Fact]
    public void Prune_deletes_only_old_terminal_jobs_and_their_records_and_logs()
    {
        using var f = new JobFixture();
        var oldCompleted = f.Submit("old-completed");
        var completedRun = f.Store.BeginNextAttempt()!;
        f.Store.Complete(new RunRef(completedRun.Job.JobId, completedRun.RunId, completedRun.Generation, completedRun.Correlation), "old output");
        var oldFailed = f.Submit("old-failed");
        var failedRun = f.Store.BeginNextAttempt()!;
        f.Store.EndUnsuccessfully(new RunRef(failedRun.Job.JobId, failedRun.RunId, failedRun.Generation, failedRun.Correlation), JobStatus.Failed, "test_failure");
        var oldCancelled = f.Submit("old-cancelled");
        new StopJob(f.Store, JobFixture.Operator, _ => { }).Execute(oldCancelled.JobId);
        var recentCompleted = f.Submit("recent-completed");
        var oldQueued = f.Submit("old-queued");
        var oldRunning = f.Submit("old-running");
        var uncertain = f.Submit("uncertain");
        Set(f, oldCompleted.JobId, "completed", 40);
        Set(f, oldFailed.JobId, "failed", 40);
        Set(f, oldCancelled.JobId, "cancelled", 40);
        Set(f, recentCompleted.JobId, "completed", 2);
        Set(f, oldQueued.JobId, "queued", 40);
        Set(f, oldRunning.JobId, "running", 40);
        Set(f, uncertain.JobId, "needs_reconciliation", 40);
        var state = Path.GetDirectoryName(f.DatabasePath)!;
        var logs = Path.Combine(state, "logs");
        new JobLogs(state).BeginRun(oldCompleted.JobId, "run", "fake")("stdout", "old\n"u8.ToArray());
        Assert.Contains("old", new JobLogs(state).Read(oldCompleted.JobId).Text);
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, oldRunning.JobId + ".log"), "active");

        var count = new PruneJob(new PruneJobs(f.Database), state).Execute(30, false);

        Assert.Equal(3, count);
        Assert.Null(f.Store.GetJob(oldCompleted.JobId));
        Assert.Null(f.Store.GetJob(oldFailed.JobId));
        Assert.Null(f.Store.GetJob(oldCancelled.JobId));
        Assert.Empty(f.Store.GetEvents(oldCancelled.JobId));
        Assert.Empty(f.Store.GetEvents(oldCompleted.JobId));
        Assert.Empty(f.Store.GetRuns(oldCompleted.JobId));
        Assert.False(File.Exists(Path.Combine(logs, oldCompleted.JobId + ".log")));
        Assert.True(File.Exists(Path.Combine(logs, oldRunning.JobId + ".log")));
        foreach (var id in new[] { recentCompleted.JobId, oldQueued.JobId, oldRunning.JobId, uncertain.JobId })
        {
            Assert.NotNull(f.Store.GetJob(id));
        }
    }

    [Fact]
    public void Active_child_pins_old_terminal_parent_and_dry_run_deletes_nothing()
    {
        using var f = new JobFixture();
        var parent = f.Submit("parent");
        Set(f, parent.JobId, "completed", 40, "session-1");
        var child = new FollowUpJob(f.Store, JobFixture.Operator, f.Accept())
            .Execute(new FollowUpRequest(parent.JobId, "next", "child")).Job!;
        var expired = f.Submit("expired");
        Set(f, expired.JobId, "completed", 40);
        var prune = new PruneJobs(f.Database);

        Assert.Equal([expired.JobId], prune.Execute(DateTimeOffset.UtcNow.AddDays(-30), true));
        Assert.NotNull(f.Store.GetJob(expired.JobId));
        Assert.Single(f.Store.GetEvents(expired.JobId));
        Assert.Equal([expired.JobId], prune.Execute(DateTimeOffset.UtcNow.AddDays(-30), false));
        Assert.NotNull(f.Store.GetJob(parent.JobId));
        Assert.NotNull(f.Store.GetJob(child.JobId));
    }

    [Fact]
    public void Unread_wake_result_is_kept_until_read()
    {
        using var f = new JobFixture();
        var wake = new WakeStore(f.Database);
        var target = wake.Register("codex:test", "codex", "thread", "", "/tmp");
        var job = f.Accept().Execute(new SubmitJobRequest("unread", "hello", null, false) { WakeKey = target.Key, WakeGeneration = target.Generation }).Job!;
        var claim = f.Store.BeginNextAttempt()!;
        f.Store.Complete(new RunRef(claim.Job.JobId, claim.RunId, claim.Generation, claim.Correlation), "result");
        Set(f, job.JobId, "completed", 40);
        var prune = new PruneJobs(f.Database);

        Assert.Empty(prune.Execute(DateTimeOffset.UtcNow.AddDays(-30), false));
        Assert.NotNull(f.Store.GetJob(job.JobId));

        wake.MarkRead(job.JobId, target.Key, target.Generation);
        Assert.Equal([job.JobId], prune.Execute(DateTimeOffset.UtcNow.AddDays(-30), false));
        Assert.Null(f.Store.GetJob(job.JobId));
    }

    static void Set(JobFixture f, string id, string status, int daysOld, string? session = null)
    {
        using var connection = new SqliteConnection($"Data Source={f.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET status=$status, updated_at=$updated, session_id=$session WHERE job_id=$id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.AddDays(-daysOld).ToString("O"));
        command.Parameters.AddWithValue("$session", (object?)session ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
