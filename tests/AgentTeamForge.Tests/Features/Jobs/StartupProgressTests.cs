using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;
using System.Text.Json;

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
        Assert.Null(StartupProgress.Read(f.Store, job.JobId, status, "codex", null, interactive: true));
    }

    [Theory]
    [InlineData("claude", true, true)]
    [InlineData("claude", false, false)]
    [InlineData("fake", true, false)]
    public void Slow_start_hint_is_diagnostic_only(string backend, bool interactive, bool hasHint)
    {
        using var f = new JobFixture();
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, f.TestProfile, f.Admission, [backend]);
        var job = accept.Execute(new SubmitJobRequest("slow", "hello", null, false) { Backend = backend }).Job!;
        var claim = f.Store.BeginNextAttempt()!;
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE runs SET started_at=$at";
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.AddSeconds(-60).ToString("O"));
            command.ExecuteNonQuery();
        }
        var progress = StartupProgress.Read(f.Store, job.JobId, JobStatus.Running, backend, null, interactive)!;
        Assert.Equal("starting", progress.Phase);
        Assert.InRange(progress.ElapsedSeconds, 60, 65);
        Assert.Equal(hasHint, progress.NoMarkerSinceLaunch);
        Assert.Equal(hasHint, progress.Hint is not null);
        if (hasHint)
        {
            Assert.Contains("No state marker since launch", progress.Hint);
            var get = new GetJob(f.Store, JobFixture.Operator, interactive).Execute(job.JobId).Job!;
            var listed = new ListJobs(f.Store, JobFixture.Operator, interactiveLaunch: interactive).Execute(new()).Page!;
            Assert.True(get.Startup!.NoMarkerSinceLaunch);
            Assert.True(listed.Jobs.Single().Startup!.NoMarkerSinceLaunch);
            using var getJson = JsonDocument.Parse(JsonSerializer.Serialize(new IpcResponse(true, Job: get), IpcJson.Default.IpcResponse));
            using var listJson = JsonDocument.Parse(JsonSerializer.Serialize(new IpcResponse(true, Page: listed), IpcJson.Default.IpcResponse));
            Assert.True(getJson.RootElement.GetProperty("job").GetProperty("startup").GetProperty("no_marker_since_launch").GetBoolean());
            Assert.True(listJson.RootElement.GetProperty("page").GetProperty("jobs")[0].GetProperty("startup").GetProperty("no_marker_since_launch").GetBoolean());
        }
        Assert.Equal(JobStatus.Running, f.Store.GetJob(job.JobId)!.Status);
        Assert.False(f.Store.IsSessionFenced(job.JobId));
        var stale = new RunRef(job.JobId, claim.RunId, claim.Generation + 1, claim.Correlation);
        f.Store.RecordStartup(stale, "submitted");
        Assert.Null(f.Store.GetRuns(job.JobId).Single().SubmittedAt);
    }

    [Theory]
    [InlineData("claude", "agent_login_required", "run `claude` and /login")]
    [InlineData("codex", "agent_login_required", "run `codex login`")]
    [InlineData("pi", "agent_login_required", "run `pi` and /login, or set the API key")]
    [InlineData("claude", "agent_first_run_required", "run `claude` once in a terminal")]
    [InlineData("codex", "agent_workspace_trust_required", "run `codex` in this workspace")]
    [InlineData("pi", "agent_first_run_required", null)]
    [InlineData("pi", "agent_workspace_trust_required", null)]
    [InlineData("fake", "agent_login_required", null)]
    public void Blocker_hint_names_the_backends_own_step(string backend, string reason, string? hint)
    {
        using var f = new JobFixture();
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, f.TestProfile, f.Admission, [backend]);
        var job = accept.Execute(new SubmitJobRequest("blocked", "hello", null, false) { Backend = backend }).Job!;
        f.Store.BeginNextAttempt();
        var progress = StartupProgress.Read(f.Store, job.JobId, JobStatus.Running, backend, reason, interactive: true)!;
        if (hint is null)
        {
            Assert.Null(progress.Hint);
        }
        else
        {
            Assert.Contains(hint, progress.Hint);
            Assert.DoesNotContain("``", progress.Hint);
        }
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
