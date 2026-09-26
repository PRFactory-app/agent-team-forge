using AgentTeamForge.DAL.Sqlite;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.DAL.Migrations;

static class Schema
{
    public const int CurrentVersion = 2;

    const string V1 = """
        CREATE TABLE schema_migrations(
            version INTEGER PRIMARY KEY,
            applied_at TEXT NOT NULL);

        CREATE TABLE jobs(
            job_id TEXT PRIMARY KEY,
            principal TEXT NOT NULL,
            team TEXT NOT NULL,
            target_agent TEXT NOT NULL,
            operation TEXT NOT NULL,
            idempotency_key TEXT NOT NULL,
            fingerprint TEXT NOT NULL,
            instruction TEXT NOT NULL,
            options TEXT NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('queued','running','completed','failed','needs_reconciliation')),
            reason_code TEXT,
            result_text TEXT,
            accepted_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(principal, team, operation, idempotency_key));

        CREATE TABLE dispatch_intents(
            job_id TEXT PRIMARY KEY REFERENCES jobs(job_id),
            state TEXT NOT NULL CHECK(state IN ('unattempted','attempted')),
            created_at TEXT NOT NULL);

        CREATE TABLE runs(
            run_id TEXT PRIMARY KEY,
            job_id TEXT NOT NULL REFERENCES jobs(job_id),
            generation INTEGER NOT NULL,
            correlation TEXT NOT NULL UNIQUE,
            state TEXT NOT NULL CHECK(state IN ('started','completed','failed','needs_reconciliation')),
            backend_pid INTEGER,
            acked INTEGER NOT NULL DEFAULT 0,
            reason_code TEXT,
            started_at TEXT NOT NULL,
            finished_at TEXT,
            UNIQUE(job_id, generation));

        CREATE TABLE events(
            seq INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id TEXT NOT NULL REFERENCES jobs(job_id),
            run_id TEXT REFERENCES runs(run_id),
            kind TEXT NOT NULL,
            created_at TEXT NOT NULL);
        """;

    // Independent wake migration: no changes to the jobs table or V1.
    const string V2Wake = """
        CREATE TABLE wake_targets(
            target_key TEXT PRIMARY KEY,
            generation INTEGER NOT NULL,
            kind TEXT NOT NULL CHECK(kind IN ('claude','codex','pi')),
            address TEXT NOT NULL,
            secret TEXT NOT NULL,
            home TEXT NOT NULL,
            notified_seq INTEGER NOT NULL DEFAULT 0,
            last_success TEXT,
            registered_at TEXT NOT NULL);
        CREATE TABLE wake_jobs(
            job_id TEXT PRIMARY KEY REFERENCES jobs(job_id),
            target_key TEXT NOT NULL REFERENCES wake_targets(target_key),
            read_at TEXT);
        CREATE INDEX wake_jobs_target ON wake_jobs(target_key, read_at);
        """;

    /// <summary>
    /// Checks the stored version before any write. A newer version is refused
    /// with the file untouched; an older known version is migrated in one transaction.
    /// </summary>
    public static void Apply(SqliteConnection connection, bool allowCreate)
    {
        var stored = ReadVersion(connection);
        if (stored > CurrentVersion)
        {
            throw new StorageException(StorageFailure.SchemaTooNew, "database schema is newer than this build");
        }

        if (stored == CurrentVersion)
        {
            return;
        }

        if (stored == 0 && !allowCreate)
        {
            throw new StorageException(StorageFailure.Unavailable, "database is not initialized");
        }

        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }

        using var tx = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        if (stored == 0)
        {
            command.CommandText = V1 + "\nINSERT INTO schema_migrations(version, applied_at) VALUES (1, $at);";
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        if (stored < 2)
        {
            command.CommandText = V2Wake + "\nINSERT INTO schema_migrations(version, applied_at) VALUES (2, $at);";
            if (!command.Parameters.Contains("$at"))
            {
                command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            }
            command.ExecuteNonQuery();
        }

        tx.Commit();
    }

    static long ReadVersion(SqliteConnection connection)
    {
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations';";
        if ((long)exists.ExecuteScalar()! == 0)
        {
            using var any = connection.CreateCommand();
            any.CommandText = "SELECT count(*) FROM sqlite_master;";
            if ((long)any.ExecuteScalar()! != 0)
            {
                throw new StorageException(StorageFailure.Unavailable, "database has unknown content");
            }

            return 0;
        }

        using var version = connection.CreateCommand();
        version.CommandText = "SELECT coalesce(max(version), 0) FROM schema_migrations;";
        return (long)version.ExecuteScalar()!;
    }
}
