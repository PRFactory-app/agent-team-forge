using AgentTeamForge.DAL.Sqlite;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.DAL.Features.Jobs;

/// <summary>
/// Atomic job persistence. Every write operation is one BEGIN IMMEDIATE
/// transaction containing its lookups, checks and writes; nothing opts into
/// deferred mode and no transaction handle leaves this class.
/// </summary>
public sealed class JobStore(JobDatabase database, DurabilityCheckpoints checkpoints)
{
    const string JobColumns = """
        j.job_id, j.principal, j.team, j.target_agent, j.idempotency_key, j.instruction, j.options,
        j.status, j.reason_code, j.result_text,
        (SELECT count(*) FROM runs r WHERE r.job_id = j.job_id)
        """;

    /// <summary>
    /// Stores job + scoped key/fingerprint + unattempted intent + acceptance event
    /// in one commit, or returns the existing job, a conflict, or queue-full.
    /// The unique scoped-key constraint is the final arbiter.
    /// </summary>
    public AcceptOutcome AcceptOrGet(NewJob job, int queueLimit) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        var existing = QuerySingle(connection, tx,
            $"SELECT {JobColumns}, j.fingerprint FROM jobs j WHERE j.principal=$p AND j.team=$t AND j.operation=$o AND j.idempotency_key=$k",
            ("$p", job.Principal), ("$t", job.Team), ("$o", job.Operation), ("$k", job.IdempotencyKey));
        if (existing is not null)
        {
            return new AcceptOutcome(
                existing.Value.Fingerprint == job.Fingerprint ? AcceptKind.Existing : AcceptKind.Conflict,
                existing.Value.Job);
        }

        var active = Scalar(connection, tx, "SELECT count(*) FROM jobs WHERE status IN ('queued','running')");
        if (active >= queueLimit)
        {
            return new AcceptOutcome(AcceptKind.QueueFull, null);
        }

        var jobId = "job_" + Guid.CreateVersion7().ToString("N");
        var now = Now();
        Execute(connection, tx, """
            INSERT INTO jobs(job_id, principal, team, target_agent, operation, idempotency_key, fingerprint,
                             instruction, options, status, accepted_at, updated_at)
            VALUES ($id, $p, $t, $a, $o, $k, $f, $i, $opt, 'queued', $now, $now);
            INSERT INTO dispatch_intents(job_id, state, created_at) VALUES ($id, 'unattempted', $now);
            INSERT INTO events(job_id, kind, created_at) VALUES ($id, 'accepted', $now);
            """,
            ("$id", jobId), ("$p", job.Principal), ("$t", job.Team), ("$a", job.TargetAgent),
            ("$o", job.Operation), ("$k", job.IdempotencyKey), ("$f", job.Fingerprint),
            ("$i", job.Instruction), ("$opt", job.Options), ("$now", now));
        if (job.WakeTargetKey is not null && job.WakeGeneration is not null)
        {
            Execute(connection, tx, """
                INSERT INTO wake_jobs(job_id, target_key)
                SELECT $id, target_key FROM wake_targets
                WHERE target_key=$key AND generation=$generation
                """, ("$id", jobId), ("$key", job.WakeTargetKey), ("$generation", job.WakeGeneration.Value));
        }
        checkpoints.Hit(DurabilityCheckpoints.AcceptBeforeCommit);
        tx.Commit();

        // The committed row as written, without a post-commit read: a read failure
        // here would otherwise be reported as a storage error for an accepted job.
        return new AcceptOutcome(AcceptKind.Accepted, new JobRecord(jobId, job.Principal, job.Team, job.TargetAgent,
            job.IdempotencyKey, job.Instruction, job.Options, JobStatus.Queued, null, null, 0));
    });

    /// <summary>
    /// Claims the oldest unattempted intent and commits the attempt-start
    /// (generation + correlation) before the caller may cause any effect.
    /// </summary>
    public AttemptClaim? BeginNextAttempt() => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        var jobId = QueryString(connection, tx,
            "SELECT job_id FROM dispatch_intents WHERE state='unattempted' ORDER BY created_at, rowid LIMIT 1");
        if (jobId is null)
        {
            return null;
        }

        var claimed = Execute(connection, tx,
            "UPDATE dispatch_intents SET state='attempted' WHERE job_id=$id AND state='unattempted'", ("$id", jobId));
        if (claimed != 1)
        {
            return null;
        }

        var generation = Scalar(connection, tx, "SELECT coalesce(max(generation), 0) + 1 FROM runs WHERE job_id=$id", ("$id", jobId));
        var runId = "run_" + Guid.CreateVersion7().ToString("N");
        var correlation = Guid.NewGuid().ToString("N");
        var now = Now();
        var moved = Execute(connection, tx, """
            INSERT INTO runs(run_id, job_id, generation, correlation, state, started_at)
            VALUES ($run, $id, $gen, $corr, 'started', $now);
            UPDATE jobs SET status='running', updated_at=$now WHERE job_id=$id AND status='queued';
            INSERT INTO events(job_id, run_id, kind, created_at) VALUES ($id, $run, 'attempt_started', $now);
            """,
            ("$run", runId), ("$id", jobId), ("$gen", generation), ("$corr", correlation), ("$now", now));
        if (moved != 3)
        {
            throw new StorageException(StorageFailure.Unavailable, "job was not in queued state for its unattempted intent");
        }

        tx.Commit();
        return new AttemptClaim(GetJob(connection, null, jobId)!, runId, generation, correlation);
    });

    /// <summary>Diagnostic evidence only; never changes job state.</summary>
    public void RecordBackendEvidence(RunRef run, int? pid, bool acked) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        Execute(connection, tx, """
            UPDATE runs SET backend_pid=coalesce($pid, backend_pid), acked=max(acked, $acked)
            WHERE run_id=$run AND job_id=$id AND generation=$gen AND correlation=$corr AND state='started'
            """,
            ("$pid", pid), ("$acked", acked ? 1 : 0), ("$run", run.RunId), ("$id", run.JobId),
            ("$gen", run.Generation), ("$corr", run.Correlation));
        tx.Commit();
        return 0;
    });

    /// <summary>
    /// Fenced completion: only the current started run with matching
    /// generation/correlation can store result + terminal status + event, together.
    /// </summary>
    public bool Complete(RunRef run, string resultText) =>
        Finish(run, "completed", JobStatus.Completed, null, resultText, "completed", DurabilityCheckpoints.CompleteBeforeCommit);

    /// <summary>
    /// Fenced non-success end of an attempted run. Never recreates a dispatch intent.
    /// </summary>
    public bool EndUnsuccessfully(RunRef run, string terminalStatus, string reasonCode)
    {
        if (terminalStatus is not (JobStatus.Failed or JobStatus.NeedsReconciliation))
        {
            throw new ArgumentOutOfRangeException(nameof(terminalStatus));
        }

        return Finish(run, terminalStatus, terminalStatus, reasonCode, null, terminalStatus, null);
    }

    bool Finish(RunRef run, string runState, string jobStatus, string? reason, string? result, string eventKind, string? checkpoint) =>
        Write(connection =>
        {
            using var tx = connection.BeginTransaction(deferred: false);
            var now = Now();
            var runUpdated = Execute(connection, tx, """
                UPDATE runs SET state=$state, reason_code=$reason, finished_at=$now
                WHERE run_id=$run AND job_id=$id AND generation=$gen AND correlation=$corr AND state='started'
                  AND generation = (SELECT max(generation) FROM runs WHERE job_id=$id)
                """,
                ("$state", runState), ("$reason", reason), ("$now", now), ("$run", run.RunId),
                ("$id", run.JobId), ("$gen", run.Generation), ("$corr", run.Correlation));
            if (runUpdated != 1)
            {
                return false;
            }

            var jobUpdated = Execute(connection, tx, """
                UPDATE jobs SET status=$status, reason_code=$reason, result_text=$result, updated_at=$now
                WHERE job_id=$id AND status='running';
                """,
                ("$status", jobStatus), ("$reason", reason), ("$result", result), ("$now", now), ("$id", run.JobId));
            if (jobUpdated != 1)
            {
                return false;
            }

            Execute(connection, tx,
                "INSERT INTO events(job_id, run_id, kind, created_at) VALUES ($id, $run, $kind, $now)",
                ("$id", run.JobId), ("$run", run.RunId), ("$kind", eventKind), ("$now", now));
            if (checkpoint is not null)
            {
                checkpoints.Hit(checkpoint);
            }

            tx.Commit();
            return true;
        });

    /// <summary>
    /// Startup recovery: every started attempt without committed completion is
    /// uncertain and is quarantined; nothing is re-queued.
    /// </summary>
    public IReadOnlyList<string> QuarantineUncertainAttempts() => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        var jobs = new List<string>();
        using (var select = Command(connection, tx, "SELECT job_id FROM runs WHERE state='started'"))
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                jobs.Add(reader.GetString(0));
            }
        }

        var now = Now();
        foreach (var jobId in jobs)
        {
            Execute(connection, tx, """
                UPDATE runs SET state='needs_reconciliation', reason_code='daemon_restart_uncertain', finished_at=$now
                WHERE job_id=$id AND state='started';
                UPDATE jobs SET status='needs_reconciliation', reason_code='daemon_restart_uncertain', updated_at=$now
                WHERE job_id=$id AND status='running';
                INSERT INTO events(job_id, kind, created_at) VALUES ($id, 'needs_reconciliation', $now);
                """,
                ("$id", jobId), ("$now", now));
        }

        tx.Commit();
        return (IReadOnlyList<string>)jobs;
    });

    public JobRecord? GetJob(string jobId) => Read(connection => GetJob(connection, null, jobId));

    public IReadOnlyList<EventRecord> GetEvents(string jobId) => Read(connection =>
    {
        using var command = Command(connection, null, "SELECT seq, job_id, run_id, kind FROM events WHERE job_id=$id ORDER BY seq", ("$id", jobId));
        using var reader = command.ExecuteReader();
        var events = new List<EventRecord>();
        while (reader.Read())
        {
            events.Add(new EventRecord(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)));
        }

        return (IReadOnlyList<EventRecord>)events;
    });

    public IReadOnlyList<RunRecord> GetRuns(string jobId) => Read(connection =>
    {
        using var command = Command(connection, null,
            "SELECT run_id, generation, correlation, state, acked, backend_pid, reason_code FROM runs WHERE job_id=$id ORDER BY generation", ("$id", jobId));
        using var reader = command.ExecuteReader();
        var runs = new List<RunRecord>();
        while (reader.Read())
        {
            runs.Add(new RunRecord(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4) != 0, reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return (IReadOnlyList<RunRecord>)runs;
    });

    /// <summary>
    /// One output-bounded keyset page of a principal/team's jobs, newest job ID first.
    /// A single read-only statement: it takes no write lock and changes no row,
    /// intent, event or cursor. Each call is an independent committed read (live
    /// keyset, not a snapshot). No `(principal, team, job_id)` index exists, so DB
    /// work still grows with the caller's total jobs; only the returned rows are capped.
    /// </summary>
    public IReadOnlyList<JobSummaryRecord> ListJobs(string principal, string team, string? status, string? beforeJobId, int take) => Read(connection =>
    {
        using var command = Command(connection, null, """
            SELECT j.job_id, j.status, j.reason_code, (SELECT count(*) FROM runs r WHERE r.job_id = j.job_id), j.accepted_at, j.updated_at
            FROM jobs j
            WHERE j.principal=$p AND j.team=$t
              AND ($status IS NULL OR j.status=$status)
              AND ($before IS NULL OR j.job_id < $before)
            ORDER BY j.job_id DESC
            LIMIT $take
            """,
            ("$p", principal), ("$t", team), ("$status", status), ("$before", beforeJobId), ("$take", take));
        using var reader = command.ExecuteReader();
        var jobs = new List<JobSummaryRecord>();
        while (reader.Read())
        {
            jobs.Add(new JobSummaryRecord(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3), reader.GetString(4), reader.GetString(5)));
        }

        return (IReadOnlyList<JobSummaryRecord>)jobs;
    });

    public long CountUnattemptedIntents() => Read(connection => Scalar(connection, null, "SELECT count(*) FROM dispatch_intents WHERE state='unattempted'"));

    static JobRecord? GetJob(SqliteConnection connection, SqliteTransaction? tx, string jobId) =>
        QuerySingle(connection, tx, $"SELECT {JobColumns}, j.fingerprint FROM jobs j WHERE j.job_id=$id", ("$id", jobId))?.Job;

    static (JobRecord Job, string Fingerprint)? QuerySingle(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string, object?)[] parameters)
    {
        using var command = Command(connection, tx, sql, parameters);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var job = new JobRecord(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetInt32(10));
        return (job, reader.GetString(11));
    }

    static string? QueryString(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string, object?)[] parameters)
    {
        using var command = Command(connection, tx, sql, parameters);
        return command.ExecuteScalar() as string;
    }

    static long Scalar(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string, object?)[] parameters)
    {
        using var command = Command(connection, tx, sql, parameters);
        return (long)command.ExecuteScalar()!;
    }

    static int Execute(SqliteConnection connection, SqliteTransaction tx, string sql, params (string, object?)[] parameters)
    {
        using var command = Command(connection, tx, sql, parameters);
        return command.ExecuteNonQuery();
    }

    static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    static string Now() => DateTimeOffset.UtcNow.ToString("O");

    T Write<T>(Func<SqliteConnection, T> operation) => Run(operation);

    T Read<T>(Func<SqliteConnection, T> operation) => Run(operation);

    T Run<T>(Func<SqliteConnection, T> operation)
    {
        try
        {
            using var connection = database.OpenConnection();
            return operation(connection);
        }
        catch (SqliteException ex)
        {
            throw StorageException.From(ex);
        }
    }
}
