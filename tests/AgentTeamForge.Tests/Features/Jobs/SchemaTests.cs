using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class SchemaTests
{
    [Fact]
    public void Newer_schema_is_refused_and_left_unchanged()
    {
        using var dir = new TempStateDir();
        var path = dir.File("jobs.db");
        JobDatabase.Create(path, TimeSpan.FromSeconds(1));
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO schema_migrations(version, applied_at) VALUES (99, 'future')";
            command.ExecuteNonQuery();
        }

        var before = File.ReadAllBytes(path);
        var ex = Assert.Throws<StorageException>(() => JobDatabase.Open(path, TimeSpan.FromSeconds(1)));

        Assert.Equal(StorageFailure.SchemaTooNew, ex.Failure);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Version_1_database_is_migrated_keeping_its_jobs()
    {
        using var dir = new TempStateDir();
        var path = dir.File("jobs.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = AgentTeamForge.DAL.Migrations.Schema.V1 + """
                INSERT INTO schema_migrations(version, applied_at) VALUES (1, 'v1');
                INSERT INTO jobs(job_id, principal, team, target_agent, operation, idempotency_key, fingerprint,
                                 instruction, options, status, result_text, accepted_at, updated_at)
                VALUES ('job_old', 'local-operator', 'spike-team', 'fake-agent', 'job_submit', 'k', 'fp',
                        'hi', 'behavior=complete;hold=0', 'completed', 'kept', 'a', 'a');
                INSERT INTO runs(run_id, job_id, generation, correlation, state, started_at, finished_at)
                VALUES ('run_old', 'job_old', 1, 'corr_old', 'completed', 'a', 'a');
                INSERT INTO events(job_id, run_id, kind, created_at)
                VALUES ('job_old', 'run_old', 'completed', 'a');
                """;
            command.ExecuteNonQuery();
        }

        var store = new JobStore(JobDatabase.Open(path, TimeSpan.FromSeconds(1)), DurabilityCheckpoints.None);

        var job = store.GetJob("job_old")!;
        Assert.Equal(("kept", "fake", null, null), (job.ResultText, job.Backend, job.SessionId, job.ParentJobId));
        Assert.Equal("completed", Assert.Single(store.GetRuns("job_old")).State);
        using var check = new SqliteConnection($"Data Source={path};Pooling=False");
        check.Open();
        using var version = check.CreateCommand();
        version.CommandText = "SELECT max(version) FROM schema_migrations";
        Assert.Equal((long)AgentTeamForge.DAL.Migrations.Schema.CurrentVersion, (long)version.ExecuteScalar()!);
        version.CommandText = "PRAGMA foreign_key_check";
        using var violations = version.ExecuteReader();
        Assert.False(violations.Read());
    }

    [Fact]
    public void Version_3_database_rebuild_keeps_runs_and_wake_rows_and_allows_cancelled()
    {
        using var dir = new TempStateDir();
        var path = dir.File("jobs.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = AgentTeamForge.DAL.Migrations.Schema.V1 + AgentTeamForge.DAL.Migrations.Schema.V2 + AgentTeamForge.DAL.Migrations.Schema.V3 + """
                INSERT INTO schema_migrations(version, applied_at) VALUES (3, 'v3');
                INSERT INTO jobs(job_id, principal, team, target_agent, operation, idempotency_key, fingerprint,
                                 instruction, options, status, accepted_at, updated_at, session_id)
                VALUES ('job_old', 'local-operator', 'spike-team', 'fake-agent', 'job_submit', 'k', 'fp',
                        'hi', 'behavior=complete;hold=0', 'running', 'a', 'a', 's1');
                INSERT INTO runs(run_id, job_id, generation, correlation, state, started_at)
                VALUES ('run_old', 'job_old', 1, 'corr_old', 'started', 'a');
                INSERT INTO wake_targets(target_key, generation, kind, address, secret, home, registered_at)
                VALUES ('t', 1, 'codex', 'a', '', '/tmp', 'a');
                INSERT INTO wake_jobs(job_id, target_key) VALUES ('job_old', 't');
                """;
            command.ExecuteNonQuery();
        }

        var store = new JobStore(JobDatabase.Open(path, TimeSpan.FromSeconds(1)), DurabilityCheckpoints.None);
        Assert.Equal(JobStatus.Cancelled, store.Cancel("job_old", "local-operator", "spike-team").Job!.Status);
        Assert.Equal(JobStatus.Cancelled, Assert.Single(store.GetRuns("job_old")).State);

        using var check = new SqliteConnection($"Data Source={path};Pooling=False");
        check.Open();
        using var query = check.CreateCommand();
        query.CommandText = "SELECT count(*) FROM wake_jobs WHERE job_id='job_old'";
        Assert.Equal(1L, (long)query.ExecuteScalar()!);
        query.CommandText = "PRAGMA foreign_key_check";
        using var violations = query.ExecuteReader();
        Assert.False(violations.Read());
    }

    [Fact]
    public void Version_5_database_migrates_to_6_without_losing_jobs_or_runs()
    {
        using var dir = new TempStateDir();
        var path = dir.File("jobs.db");
        var database = JobDatabase.Create(path, TimeSpan.FromSeconds(1));
        var store = new JobStore(database, DurabilityCheckpoints.None);
        var accepted = store.AcceptOrGet(new NewJob("principal", "team", "agent", "job_submit", "key", "original-fingerprint", "instruction", "options"), 10);
        var jobId = accepted.Job!.JobId;
        var claim = store.BeginNextAttempt()!;
        Assert.True(store.Complete(new RunRef(jobId, claim.RunId, claim.Generation, claim.Correlation), "kept"));

        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP INDEX jobs_lead_session;
                ALTER TABLE jobs DROP COLUMN lead_session_id;
                DROP TABLE lead_sessions;
                DELETE FROM schema_migrations WHERE version=7;
                ALTER TABLE jobs DROP COLUMN queue_deadline;
                ALTER TABLE jobs DROP COLUMN timeout_s;
                DELETE FROM schema_migrations WHERE version=6;
                """;
            command.ExecuteNonQuery();
        }

        var migrated = new JobStore(JobDatabase.Open(path, TimeSpan.FromSeconds(1)), DurabilityCheckpoints.None);
        Assert.Equal((JobStatus.Completed, "kept", null),
            (migrated.GetJob(jobId)!.Status, migrated.GetJob(jobId)!.ResultText, migrated.GetJob(jobId)!.TimeoutSeconds));
        Assert.Equal("completed", Assert.Single(migrated.GetRuns(jobId)).State);
        Assert.Equal(AcceptKind.Existing, migrated.AcceptOrGet(new NewJob("principal", "team", "agent", "job_submit", "key", "original-fingerprint", "instruction", "options"), 10).Kind);
        using var check = new SqliteConnection($"Data Source={path};Pooling=False");
        check.Open();
        using var query = check.CreateCommand();
        query.CommandText = "PRAGMA foreign_key_check";
        using var violations = query.ExecuteReader();
        Assert.False(violations.Read());
    }

    [Fact]
    public void Missing_database_is_an_error_not_new_empty_state()
    {
        using var dir = new TempStateDir();
        var path = dir.File("jobs.db");

        Assert.Throws<StorageException>(() => JobDatabase.Open(path, TimeSpan.FromSeconds(1)));
        Assert.False(File.Exists(path));
    }
}
