using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class StartupProgressTests
{
    [Fact]
    public async Task Evidence_during_start_and_delivery_is_persisted_and_projected()
    {
        using var f = new JobFixture();
        var job = f.Submit("startup");
        var claim = f.Store.BeginNextAttempt()!;
        void Check(string? phase)
        {
            var get = f.Get().Execute(job.JobId).Job!.Startup;
            var list = f.List().Execute(new()).Page!.Jobs.Single().Startup;
            Assert.Equal(phase, get?.Phase);
            Assert.Equal(get, list);
            Assert.Equal(get, f.Get().List().Jobs!.Single().Startup);
        }
        Check("starting");
        var backend = new ProgressBackend(Check);
        using var dispatch = new DispatchJob(f.Store, backend, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { });
        await dispatch.RunAttemptAsync(claim, TestContext.Current.CancellationToken);
        Check(null);
        var run = f.Store.GetRuns(job.JobId).Single();
        Assert.NotNull(run.ReadyAt);
        Assert.NotNull(run.SubmittedAt);
        Assert.NotNull(run.AcknowledgedAt);
        Assert.Equal(JobStatus.Completed, f.Store.GetJob(job.JobId)!.Status);
        // Late and stale diagnostic callbacks cannot rewrite a finished run.
        f.Store.RecordStartup(new(job.JobId, claim.RunId, claim.Generation, claim.Correlation), "ready");
        Assert.Equal(run, f.Store.GetRuns(job.JobId).Single());
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    [InlineData(JobStatus.NeedsReconciliation)]
    public void Terminal_status_never_projects_startup(string status)
    {
        using var f = new JobFixture();
        var job = f.Submit("terminal");
        f.Store.BeginNextAttempt();
        Assert.Null(StartupProgress.Read(f.Store, job.JobId, status, "codex", null));
    }

    [Theory]
    [InlineData("claude", true)]
    [InlineData("fake", false)]
    public void Slow_start_hint_is_diagnostic_only(string backend, bool hasHint)
    {
        using var f = new JobFixture();
        var job = f.Submit("slow");
        var claim = f.Store.BeginNextAttempt()!;
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE runs SET started_at=$at";
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.AddSeconds(-60).ToString("O"));
            command.ExecuteNonQuery();
        }
        var progress = StartupProgress.Read(f.Store, job.JobId, JobStatus.Running, backend, null)!;
        Assert.Equal("starting", progress.Phase);
        Assert.InRange(progress.ElapsedSeconds, 60, 65);
        Assert.Equal(hasHint, progress.Hint is not null);
        Assert.Equal(JobStatus.Running, f.Store.GetJob(job.JobId)!.Status);
        Assert.False(f.Store.IsSessionFenced(job.JobId));
        var stale = new RunRef(job.JobId, claim.RunId, claim.Generation + 1, claim.Correlation);
        f.Store.RecordStartup(stale, "submitted");
        Assert.Null(f.Store.GetRuns(job.JobId).Single().SubmittedAt);
    }

    sealed class ProgressBackend(Action<string?> check) : IJobBackend
    {
        public IBackendRun Start(BackendRequest request)
        {
            request.StartupProgress!("ready");
            check("ready");
            return new Run(request, check);
        }
    }

    sealed class Run(BackendRequest request, Action<string?> check) : IBackendRun
    {
        public int? ProcessId => null;
        public Task DeliverAsync(CancellationToken cancellationToken)
        {
            request.StartupProgress!("submitted");
            check("submitted");
            return Task.CompletedTask;
        }
        public async IAsyncEnumerable<BackendEvidence> ReadEvidenceAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new BackendEvidence.Ack(request.Correlation);
            await Task.Yield();
            check(null);
            yield return new BackendEvidence.Result(request.Correlation, "done");
        }
        public void TerminateOwnedChild() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
