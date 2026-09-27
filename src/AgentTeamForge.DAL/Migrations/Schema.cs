using AgentTeamForge.DAL.Sqlite;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.DAL.Migrations;

static class Schema
{
    public const int CurrentVersion = 24;

    internal const string V1 = """
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

    /// <summary>v2: per-job backend, working directory, follow-up parent and native session.</summary>
    internal const string V2 = """
        ALTER TABLE jobs ADD COLUMN backend TEXT NOT NULL DEFAULT 'fake';
        ALTER TABLE jobs ADD COLUMN cwd TEXT;
        ALTER TABLE jobs ADD COLUMN parent_job_id TEXT REFERENCES jobs(job_id);
        ALTER TABLE jobs ADD COLUMN session_id TEXT;
        """;

    /// <summary>v3: wake routing targets and per-job unread state.</summary>
    internal const string V3 = """
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

    /// <summary>v4: opt-in per-job git worktree.</summary>
    internal const string V4 = """
        ALTER TABLE jobs ADD COLUMN worktree_path TEXT;
        ALTER TABLE jobs ADD COLUMN worktree_branch TEXT;
        ALTER TABLE jobs ADD COLUMN worktree_base TEXT;
        """;

    /// <summary>v5: cancelled job/run state. SQLite cannot extend a CHECK constraint, so both tables are rebuilt.</summary>
    const string V5 = """
        CREATE TABLE jobs_new(
            job_id TEXT PRIMARY KEY, principal TEXT NOT NULL, team TEXT NOT NULL,
            target_agent TEXT NOT NULL, operation TEXT NOT NULL, idempotency_key TEXT NOT NULL,
            fingerprint TEXT NOT NULL, instruction TEXT NOT NULL, options TEXT NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('queued','running','completed','failed','needs_reconciliation','cancelled')),
            reason_code TEXT, result_text TEXT, accepted_at TEXT NOT NULL, updated_at TEXT NOT NULL,
            backend TEXT NOT NULL DEFAULT 'fake', cwd TEXT,
            parent_job_id TEXT REFERENCES jobs(job_id), session_id TEXT,
            worktree_path TEXT, worktree_branch TEXT, worktree_base TEXT,
            UNIQUE(principal, team, operation, idempotency_key));
        INSERT INTO jobs_new SELECT * FROM jobs;
        CREATE TABLE runs_new(
            run_id TEXT PRIMARY KEY, job_id TEXT NOT NULL REFERENCES jobs(job_id),
            generation INTEGER NOT NULL, correlation TEXT NOT NULL UNIQUE,
            state TEXT NOT NULL CHECK(state IN ('started','completed','failed','needs_reconciliation','cancelled')),
            backend_pid INTEGER, acked INTEGER NOT NULL DEFAULT 0, reason_code TEXT,
            started_at TEXT NOT NULL, finished_at TEXT, UNIQUE(job_id, generation));
        INSERT INTO runs_new SELECT * FROM runs;
        DROP TABLE runs;
        DROP TABLE jobs;
        ALTER TABLE jobs_new RENAME TO jobs;
        ALTER TABLE runs_new RENAME TO runs;
        """;

    /// <summary>v6: optional running timeout and queued expiry per job.</summary>
    const string V6 = """
        ALTER TABLE jobs ADD COLUMN timeout_s INTEGER;
        ALTER TABLE jobs ADD COLUMN queue_deadline TEXT;
        """;

    const string V7 = """
        CREATE TABLE lead_sessions(
            session_id TEXT PRIMARY KEY,
            workspace TEXT NOT NULL,
            binding_key TEXT NOT NULL,
            lead_token TEXT NOT NULL,
            wake_key TEXT,
            updated_at TEXT NOT NULL);
        CREATE INDEX lead_sessions_workspace ON lead_sessions(workspace, updated_at);
        ALTER TABLE jobs ADD COLUMN lead_session_id TEXT;
        CREATE INDEX jobs_lead_session ON jobs(lead_session_id, job_id);
        """;

    /// <summary>v8: accepted PRFactory work and its stable member submissions.</summary>
    const string V8 = """
        CREATE TABLE prfactory_teams(
            server TEXT NOT NULL, work_item_id TEXT NOT NULL, claimed_json TEXT NOT NULL,
            state TEXT NOT NULL DEFAULT 'claimed', uploaded INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
            PRIMARY KEY(server, work_item_id));
        CREATE TABLE prfactory_members(
            server TEXT NOT NULL, work_item_id TEXT NOT NULL, member TEXT NOT NULL,
            turn INTEGER NOT NULL, job_id TEXT NOT NULL,
            PRIMARY KEY(server, work_item_id, member, turn),
            FOREIGN KEY(server, work_item_id) REFERENCES prfactory_teams(server, work_item_id));
        """;

    /// <summary>v9: durable external teams, members and messages.</summary>
    const string V9 = """
        ALTER TABLE lead_sessions ADD COLUMN closed_at TEXT;
        ALTER TABLE wake_targets ADD COLUMN external_notified_seq INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE wake_targets ADD COLUMN last_external_success TEXT;
        CREATE TABLE external_teams(
            team_id TEXT PRIMARY KEY,
            owner_key TEXT NOT NULL UNIQUE,
            lead_session_id TEXT UNIQUE REFERENCES lead_sessions(session_id),
            wake_key TEXT,
            created_at TEXT NOT NULL,
            closed_at TEXT);
        CREATE TABLE external_members(
            member_id TEXT PRIMARY KEY,
            team_id TEXT NOT NULL REFERENCES external_teams(team_id) ON DELETE CASCADE,
            name TEXT NOT NULL,
            note TEXT NOT NULL,
            ticket_hash TEXT NOT NULL UNIQUE,
            ticket_expires TEXT NOT NULL,
            ticket_used_at TEXT,
            token_hash TEXT UNIQUE,
            active INTEGER NOT NULL DEFAULT 0,
            wake_key TEXT,
            created_at TEXT NOT NULL,
            left_at TEXT,
            UNIQUE(team_id,name));
        CREATE TABLE external_messages(
            seq INTEGER PRIMARY KEY AUTOINCREMENT,
            team_id TEXT NOT NULL REFERENCES external_teams(team_id) ON DELETE CASCADE,
            sender TEXT NOT NULL,
            recipient TEXT NOT NULL,
            text TEXT NOT NULL,
            created_at TEXT NOT NULL,
            read_at TEXT,
            wake_key TEXT);
        CREATE INDEX external_messages_inbox ON external_messages(team_id,recipient,seq);
        CREATE INDEX external_messages_wake ON external_messages(wake_key,read_at,seq);
        """;

    const string V10 = """
        CREATE TABLE prfactory_external(
            server TEXT NOT NULL, work_item_id TEXT NOT NULL, member TEXT NOT NULL,
            actual_name TEXT NOT NULL, team_id TEXT NOT NULL, ticket_token TEXT NOT NULL,
            ticket_expires TEXT NOT NULL, ticket_uploaded INTEGER NOT NULL DEFAULT 0,
            reply_seq INTEGER NOT NULL DEFAULT 0, closed INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(server, work_item_id, member),
            FOREIGN KEY(server, work_item_id) REFERENCES prfactory_teams(server, work_item_id));
        CREATE TABLE external_delivery_keys(
            team_id TEXT NOT NULL, command_id TEXT NOT NULL,
            PRIMARY KEY(team_id, command_id));
        CREATE TABLE prfactory_command_receipts(
            server TEXT NOT NULL, work_item_id TEXT NOT NULL, command_id TEXT NOT NULL,
            accepted INTEGER NOT NULL, reason TEXT,
            PRIMARY KEY(server, work_item_id, command_id),
            FOREIGN KEY(server, work_item_id) REFERENCES prfactory_teams(server, work_item_id));
        """;

    /// <summary>v11: durable sender positions and read cursors survive message retention.</summary>
    const string V11 = """
        ALTER TABLE external_messages ADD COLUMN sender_seq INTEGER NOT NULL DEFAULT 0;
        WITH numbered AS (
            SELECT seq, ROW_NUMBER() OVER (PARTITION BY team_id,recipient,sender ORDER BY seq) AS position
            FROM external_messages)
        UPDATE external_messages SET sender_seq=(SELECT position FROM numbered WHERE numbered.seq=external_messages.seq);
        CREATE TABLE external_sender_cursors(
            team_id TEXT NOT NULL REFERENCES external_teams(team_id) ON DELETE CASCADE,
            recipient TEXT NOT NULL,
            sender TEXT NOT NULL,
            high_water INTEGER NOT NULL DEFAULT 0,
            cursor INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(team_id,recipient,sender));
        INSERT INTO external_sender_cursors(team_id,recipient,sender,high_water,cursor)
        SELECT team_id,recipient,sender,MAX(sender_seq),COALESCE(MAX(CASE WHEN read_at IS NOT NULL THEN sender_seq END),0)
        FROM external_messages GROUP BY team_id,recipient,sender;
        CREATE INDEX external_messages_sender_position ON external_messages(team_id,recipient,sender,sender_seq);
        """;

    /// <summary>v12: durable client identity and server acceptance state.</summary>
    const string V12 = """
        ALTER TABLE prfactory_teams ADD COLUMN machine_id TEXT;
        ALTER TABLE prfactory_teams ADD COLUMN atf_job_id TEXT;
        ALTER TABLE prfactory_teams ADD COLUMN acceptance_state TEXT NOT NULL DEFAULT 'legacy';
        """;

    internal const string V13 = """
        ALTER TABLE jobs ADD COLUMN session_fenced INTEGER NOT NULL DEFAULT 0;
        UPDATE jobs SET session_fenced=1 WHERE status='needs_reconciliation';
        """;

    internal const string V14 = """
        ALTER TABLE runs ADD COLUMN ready_at TEXT;
        ALTER TABLE runs ADD COLUMN submitted_at TEXT;
        ALTER TABLE runs ADD COLUMN acknowledged_at TEXT;
        """;

    internal const string V15 = "ALTER TABLE wake_targets ADD COLUMN active INTEGER NOT NULL DEFAULT 1;";

    const string V16 = """
        CREATE TABLE prfactory_pending_commands(
            server TEXT NOT NULL, work_item_id TEXT NOT NULL, command_id TEXT NOT NULL,
            payload TEXT NOT NULL, parent_job TEXT,
            PRIMARY KEY(server, work_item_id, command_id));
        CREATE TABLE prfactory_stream_positions(
            server TEXT NOT NULL, work_item_id TEXT NOT NULL, job_id TEXT NOT NULL,
            offset INTEGER NOT NULL DEFAULT 0, seq INTEGER NOT NULL DEFAULT 0,
            status TEXT, pending TEXT, result_offset INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(server, work_item_id, job_id));
        CREATE TABLE prfactory_killed_members(
            server TEXT NOT NULL, work_item_id TEXT NOT NULL, member TEXT NOT NULL,
            PRIMARY KEY(server, work_item_id, member));
        """;

    internal const string V17 = """
        CREATE TABLE prfactory_artefact_delivery(
            server TEXT NOT NULL, work_item_id TEXT NOT NULL,
            payload TEXT, failure TEXT,
            PRIMARY KEY(server, work_item_id),
            FOREIGN KEY(server, work_item_id) REFERENCES prfactory_teams(server, work_item_id));
        """;

    /// <summary>v18: PRFactory server disposition and pending-stop authority (wave 2 slice 4).</summary>
    internal const string V18 = PRFactoryAuthorityMigration.Sql;

    /// <summary>v19: PRFactory team workspaces and child integration intents (slice 5).</summary>
    internal const string V19 = PRFactoryWorkspaceMigration.Sql;

    /// <summary>v20: account usage windows, parked turns and claim reservations (slice 9a).</summary>
    internal const string V20 = AccountAdmissionMigration.Sql;

    /// <summary>v21: PRFactory branch publication intents and verified receipts (slice 6).</summary>
    internal const string V21 = PRFactoryPublicationMigration.Sql;

    /// <summary>v22: human waits (slice 3) plus their exact, replayable PRFactory stream batches.</summary>
    internal const string V22 = HumanWaitMigration.Sql + """
        CREATE TABLE prfactory_human_stream(
            server TEXT NOT NULL, work_item_id TEXT NOT NULL, agent TEXT NOT NULL,
            question_id TEXT NOT NULL REFERENCES human_waits(question_id), status TEXT NOT NULL,
            seq INTEGER NOT NULL, batch TEXT NOT NULL, uploaded INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(question_id, status), UNIQUE(server, work_item_id, agent, seq));
        """;

    internal const string V23 = """
        CREATE TABLE prfactory_attachment_batches(
            server TEXT NOT NULL, work_item_id TEXT NOT NULL,
            PRIMARY KEY(server, work_item_id));
        CREATE TABLE prfactory_attachments(
            server TEXT NOT NULL, work_item_id TEXT NOT NULL, client_key TEXT NOT NULL,
            content_type TEXT NOT NULL, payload BLOB NOT NULL, uploaded INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(server, work_item_id, client_key),
            FOREIGN KEY(server, work_item_id) REFERENCES prfactory_attachment_batches(server, work_item_id));
        """;

    internal const string V24 = """
        CREATE TABLE native_codex_attempts(
            job_id TEXT PRIMARY KEY REFERENCES jobs(job_id),
            thread_id TEXT NOT NULL,
            codex_home TEXT NOT NULL,
            correlation TEXT NOT NULL,
            submission_id TEXT,
            state TEXT NOT NULL CHECK(state IN ('sent','received','settled')),
            created_at TEXT NOT NULL);
        CREATE INDEX native_codex_unresolved ON native_codex_attempts(thread_id, state);
        """;

    static readonly string[] Migrations = [V1, V2, V3, V4, V5, V6, V7, V8, V9, V10, V11, V12, V13, V14, V15, V16, V17, V18, V19, V20, V21, V22, V23, V24];

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

        // v5 replaces tables referenced by foreign keys; enforcement resumes after commit.
        if (stored < 5)
        {
            using var foreignKeys = connection.CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_keys=OFF;";
            foreignKeys.ExecuteNonQuery();
        }

        using var tx = connection.BeginTransaction(deferred: false);
        var at = DateTimeOffset.UtcNow.ToString("O");
        for (var version = stored + 1; version <= CurrentVersion; version++)
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = Migrations[version - 1] + "\nINSERT INTO schema_migrations(version, applied_at) VALUES ($v, $at);";
            command.Parameters.AddWithValue("$v", version);
            command.Parameters.AddWithValue("$at", at);
            command.ExecuteNonQuery();
        }

        tx.Commit();
        if (stored < 5)
        {
            using var foreignKeys = connection.CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_keys=ON;";
            foreignKeys.ExecuteNonQuery();
        }
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
