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
    public (string SessionId, string Workspace)? LeadForJob(string jobId)
    {
        using var db = database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT s.session_id,s.workspace FROM jobs j JOIN lead_sessions s ON s.session_id=j.lead_session_id WHERE j.job_id=$id AND s.closed_at IS NULL";
        command.Parameters.AddWithValue("$id", jobId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    const string JobColumns = """
        j.job_id, j.principal, j.team, j.target_agent, j.idempotency_key, j.instruction, j.options,
        j.status, j.reason_code, j.result_text,
        (SELECT count(*) FROM runs r WHERE r.job_id = j.job_id),
        j.backend, j.cwd, j.parent_job_id, j.session_id, j.worktree_path, j.worktree_branch, j.worktree_base, j.timeout_s
        """;

    const string SessionPeers = """
        SELECT k.job_id FROM jobs j LEFT JOIN jobs p ON p.job_id=j.parent_job_id
        JOIN jobs k ON k.backend=j.backend
        LEFT JOIN jobs q ON q.job_id=k.parent_job_id
        WHERE j.job_id=$id AND (k.job_id=j.job_id OR
            coalesce(k.session_id,q.session_id)=coalesce(j.session_id,p.session_id))
        """;

    static bool SessionFenced(SqliteConnection connection, SqliteTransaction? tx, string jobId) =>
        Scalar(connection, tx, $"SELECT count(*) FROM jobs WHERE session_fenced=1 AND job_id IN ({SessionPeers})", ("$id", jobId)) > 0;

    public IReadOnlyList<string> GetSessionJobs(string jobId) => Read(connection =>
    {
        using var command = Command(connection, null, SessionPeers, ("$id", jobId));
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) { ids.Add(reader.GetString(0)); }
        return (IReadOnlyList<string>)ids;
    });

    public bool IsSessionFenced(string jobId) => Read(connection => SessionFenced(connection, null, jobId));

    public void FenceSession(string jobId) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        Execute(connection, tx, "UPDATE jobs SET session_fenced=1 WHERE job_id=$id", ("$id", jobId));
        tx.Commit();
        return 0;
    });

    // Prevent a queued sibling from claiming between ownership verification and stop.
    public bool TryFenceSessionForStop(string jobId) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        if (Scalar(connection, tx, $"SELECT count(*) FROM jobs WHERE status='running' AND job_id IN ({SessionPeers})", ("$id", jobId)) > 0)
        {
            return false;
        }
        Execute(connection, tx, "UPDATE jobs SET session_fenced=1 WHERE job_id=$id", ("$id", jobId));
        tx.Commit();
        return true;
    });

    public void ReconcileStoppedJob(string jobId) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        Execute(connection, tx, "UPDATE jobs SET session_fenced=0 WHERE job_id=$id", ("$id", jobId));
        tx.Commit();
        return 0;
    });

    public void ReconcileStoppedSession(string jobId) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        Execute(connection, tx, $"UPDATE jobs SET session_fenced=0 WHERE job_id IN ({SessionPeers})", ("$id", jobId));
        tx.Commit();
        return 0;
    });

    public bool HasAcceptedKey(string principal, string team, string operation, string key) => Read(connection =>
        Scalar(connection, null, "SELECT count(*) FROM jobs WHERE principal=$p AND team=$t AND operation=$o AND idempotency_key=$k",
            ("$p", principal), ("$t", team), ("$o", operation), ("$k", key)) > 0);

    public string WorktreeRoot => Path.Combine(Path.GetDirectoryName(database.Path)!, "worktrees");

    /// <summary>
    /// Stores job + scoped key/fingerprint + unattempted intent + acceptance event
    /// in one commit, or returns the existing job, a conflict, or queue-full.
    /// The unique scoped-key constraint is the final arbiter.
    /// </summary>
    public AcceptOutcome AcceptOrGet(NewJob job, int queueLimit) => Write<AcceptOutcome>(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        var existing = QuerySingle(connection, tx,
            $"SELECT {JobColumns}, j.fingerprint FROM jobs j WHERE j.principal=$p AND j.team=$t AND j.operation=$o AND j.idempotency_key=$k",
            ("$p", job.Principal), ("$t", job.Team), ("$o", job.Operation), ("$k", job.IdempotencyKey));
        if (existing is not null)
        {
            return existing.Value.Fingerprint == job.Fingerprint
                ? new Existing(existing.Value.Job)
                : new Conflict();
        }

        if (Scalar(connection, tx, """
            SELECT count(*) FROM native_codex_attempts n JOIN jobs j ON j.job_id=n.job_id
            WHERE n.state='sent' AND j.principal=$p AND j.team=$t AND j.target_agent=$a
            """, ("$p", job.Principal), ("$t", job.Team), ("$a", job.TargetAgent)) > 0)
        {
            return new ParentNotReady();
        }
        if (Scalar(connection, tx, """
            SELECT count(*) FROM native_claude_attempts n JOIN jobs j ON j.job_id=n.job_id
            WHERE n.state IN ('posting','posted') AND j.principal=$p AND j.team=$t AND j.target_agent=$a
            """, ("$p", job.Principal), ("$t", job.Team), ("$a", job.TargetAgent)) > 0)
        {
            return new ParentNotReady();
        }

        var active = Scalar(connection, tx, "SELECT count(*) FROM jobs WHERE status IN ('queued','running')");
        if (active >= queueLimit)
        {
            return new QueueFull();
        }

        JobRecord? parent = null;
        if (job.ParentJobId is { } parentId)
        {
            parent = GetJob(connection, tx, parentId);
            if (parent is null || parent.Principal != job.Principal || parent.Team != job.Team)
            {
                return new ParentNotFound();
            }

            // Failed/needs_reconciliation parents were checked idle by the caller while
            // already terminal; an interrupt saw a running parent, so its later end is unchecked.
            var deferred = job.DeferParent && parent.Status is JobStatus.Queued or JobStatus.Running;
            if ((!deferred && parent.SessionId is null) || SessionFenced(connection, tx, parent.JobId) || !(deferred || parent.Status is JobStatus.Completed or JobStatus.Cancelled
                || (job.InterruptParent ? parent.Status == JobStatus.Running : parent.Status is JobStatus.Failed or JobStatus.NeedsReconciliation)))
            {
                return new ParentNotReady();
            }
            // N5 is checked in the acceptance transaction, including replacements and
            // ordinary resume attempts. An uncertain queue call may still present later.
            if (parent.SessionId is { } thread && Scalar(connection, tx,
                "SELECT count(*) FROM native_codex_attempts WHERE thread_id=$thread AND state='sent'", ("$thread", thread)) > 0)
            {
                return new ParentNotReady();
            }
            if (parent.SessionId is { } claudeSession && Scalar(connection, tx,
                "SELECT count(*) FROM native_claude_attempts WHERE session_id=$session AND state IN ('posting','posted')", ("$session", claudeSession)) > 0)
            {
                return new ParentNotReady();
            }
        }

        var jobId = "job_" + Guid.CreateVersion7().ToString("N");
        var worktreePath = job.CreateWorktree ? Path.Combine(WorktreeRoot, jobId) : job.WorktreePath;
        var worktreeBranch = job.CreateWorktree ? $"atf/job-{jobId}" : job.WorktreeBranch;
        var acceptedAt = DateTimeOffset.UtcNow;
        var now = acceptedAt.ToString("O");
        var queueDeadline = job.QueueTtlSeconds is int ttl ? acceptedAt.AddSeconds(ttl).ToString("O") : null;
        Execute(connection, tx, """
            INSERT INTO jobs(job_id, principal, team, target_agent, operation, idempotency_key, fingerprint,
                             instruction, options, backend, cwd, parent_job_id, worktree_path, worktree_branch, worktree_base, timeout_s, queue_deadline, lead_session_id, status, accepted_at, updated_at)
            VALUES ($id, $p, $t, $a, $o, $k, $f, $i, $opt, $b, $cwd, $parent, $wtpath, $wtbranch, $wtbase, $timeout, $deadline, $lead, 'queued', $now, $now);
            INSERT INTO dispatch_intents(job_id, state, created_at) VALUES ($id, 'unattempted', $now);
            INSERT INTO events(job_id, kind, created_at) VALUES ($id, 'accepted', $now);
            """,
            ("$id", jobId), ("$p", job.Principal), ("$t", job.Team), ("$a", job.TargetAgent),
            ("$o", job.Operation), ("$k", job.IdempotencyKey), ("$f", job.Fingerprint),
            ("$i", job.Instruction), ("$opt", job.Options), ("$b", job.Backend), ("$cwd", job.Cwd),
            ("$parent", job.ParentJobId), ("$wtpath", worktreePath), ("$wtbranch", worktreeBranch),
            ("$wtbase", job.WorktreeBase), ("$timeout", job.TimeoutSeconds),
            ("$deadline", queueDeadline), ("$lead", job.LeadSessionId), ("$now", now));
        if (job.WakeTargetKey is not null && job.WakeGeneration is not null)
        {
            Execute(connection, tx, """
                INSERT INTO wake_jobs(job_id, target_key)
                SELECT $id, target_key FROM wake_targets
                WHERE target_key=$key AND generation=$generation AND active=1
                """, ("$id", jobId), ("$key", job.WakeTargetKey), ("$generation", job.WakeGeneration.Value));
        }
        var interrupted = job.InterruptParent && parent?.Status == JobStatus.Running ? parent.JobId : null;
        if (interrupted is not null)
        {
            // The child intent and stop_job's cancellation commit together. A
            // racing completion wins before this transaction or is fenced out.
            CancelInTransaction(connection, tx, parent!, "interrupted");
        }
        checkpoints.Hit(DurabilityCheckpoints.AcceptBeforeCommit);
        tx.Commit();

        // The committed row as written, without a post-commit read: a read failure
        // here would otherwise be reported as a storage error for an accepted job.
        return new Accepted(new JobRecord(jobId, job.Principal, job.Team, job.TargetAgent,
            job.IdempotencyKey, job.Instruction, job.Options, JobStatus.Queued, null, null, 0,
            job.Backend, job.Cwd, job.ParentJobId, null)
        {
            WorktreePath = worktreePath,
            WorktreeBranch = worktreeBranch,
            WorktreeBase = job.WorktreeBase,
            TimeoutSeconds = job.TimeoutSeconds,
        }, interrupted);
    });

    /// <summary>
    /// Claims the oldest unattempted intent and commits the attempt-start
    /// (generation + correlation) before the caller may cause any effect.
    /// A follow-up is skipped while any job on its parent's native session is
    /// running (the parent itself, a job holding that session, or another
    /// follow-up of a job holding it), so turns on one session never overlap.
    /// </summary>
    public AttemptClaim? BeginNextAttempt(IReadOnlyCollection<string>? settlingJobs = null,
        Func<string[], int[], bool>? hasMarkedProcess = null, Func<string, bool>? eligible = null) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        // A terminal commit can precede backend disposal (retaining an interactive
        // tab or reaping a headless child). The dispatcher still owns those turns.
        var settling = settlingJobs?.Select((id, index) => ("$settling" + index, (object?)id)).ToArray() ?? [];
        var settlingSql = settling.Length == 0 ? "0" : "k.job_id IN (" + string.Join(",", settling.Select(p => p.Item1)) + ")";
        var candidates = new List<(string JobId, string? ParentId, string? ParentStatus, string? ParentSession, string Options)>();
        using (var command = Command(connection, tx, $"""
            SELECT i.job_id, p.job_id, p.status, p.session_id, j.options
            FROM dispatch_intents i JOIN jobs j ON j.job_id = i.job_id
            LEFT JOIN jobs p ON p.job_id = j.parent_job_id
            WHERE i.state='unattempted' AND j.status='queued'
              AND NOT EXISTS (SELECT 1 FROM jobs p WHERE p.job_id=j.parent_job_id AND p.status='queued')
              AND NOT EXISTS (SELECT 1 FROM jobs p JOIN native_codex_attempts n ON n.thread_id=p.session_id
                  WHERE p.job_id=j.parent_job_id AND n.state='sent')
              AND NOT EXISTS (SELECT 1 FROM jobs p JOIN native_claude_attempts n ON n.session_id=p.session_id
                  WHERE p.job_id=j.parent_job_id AND n.state IN ('posting','posted'))
              AND (j.queue_deadline IS NULL OR j.queue_deadline > $now) AND NOT EXISTS (
                SELECT 1 FROM jobs p JOIN jobs k ON (k.status='running' OR k.session_fenced=1 OR {settlingSql})
                WHERE p.job_id = j.parent_job_id
                  AND k.backend=p.backend AND (k.job_id = p.job_id OR k.session_id = p.session_id OR EXISTS (
                      SELECT 1 FROM jobs q WHERE q.job_id = k.parent_job_id AND q.session_id = p.session_id)))
            ORDER BY i.created_at, i.rowid
            """, [("$now", Now()), .. settling]))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                candidates.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
            }
        }
        string? jobId = null;
        foreach (var (candidateJobId, parentId, parentStatus, parentSession, options) in candidates)
        {
            // Skipped intents stay queued: an owner gate (authority, account window) may reopen later.
            if (eligible is not null && !eligible(candidateJobId)) { continue; }
            if (parentStatus == JobStatus.Failed && options.Contains(";defer=1", StringComparison.Ordinal))
            {
                if (parentSession is null || hasMarkedProcess is null) { continue; }
                using var runs = Command(connection, tx, "SELECT correlation, backend_pid FROM runs WHERE job_id=$id", ("$id", parentId));
                using var reader = runs.ExecuteReader();
                var correlations = new List<string>();
                var pids = new List<int>();
                while (reader.Read())
                {
                    correlations.Add(reader.GetString(0));
                    if (!reader.IsDBNull(1)) { pids.Add(reader.GetInt32(1)); }
                }
                if (correlations.Count == 0 || hasMarkedProcess([.. correlations], [.. pids])) { continue; }
            }
            jobId = candidateJobId;
            break;
        }
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

    /// <summary>Commit the native fence and run before invoking codex queue.</summary>
    public AttemptClaim? BeginNativeCodexAttempt(Func<JobRecord, bool> live, string codexHome, Func<string, bool>? eligible = null) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        var candidates = new List<JobRecord>();
        using (var command = Command(connection, tx, $"SELECT {JobColumns} FROM jobs j JOIN dispatch_intents i ON i.job_id=j.job_id WHERE i.state='unattempted' AND j.status='queued' AND (j.queue_deadline IS NULL OR j.queue_deadline>$now) AND instr(j.options,';native_codex=1')>0 ORDER BY i.created_at,i.rowid", ("$now", Now())))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) { candidates.Add(ReadJob(reader)); }
        }
        foreach (var job in candidates)
        {
            if (eligible is not null && !eligible(job.JobId)) { continue; }
            if (job.ParentJobId is not { } parentId || GetJob(connection, tx, parentId) is not { SessionId: { } thread } parent
                || parent.Backend != "codex" || SessionFenced(connection, tx, parentId)
                || Scalar(connection, tx, "SELECT count(*) FROM native_codex_attempts WHERE thread_id=$thread AND state='sent'", ("$thread", thread)) > 0
                || Scalar(connection, tx, """
                    SELECT count(*) FROM native_codex_attempts n JOIN jobs k ON k.job_id=n.job_id
                    WHERE n.state='sent' AND k.principal=$p AND k.team=$t AND k.target_agent=$a
                    """, ("$p", job.Principal), ("$t", job.Team), ("$a", job.TargetAgent)) > 0
                || !live(parent)) { continue; }
            var runId = "run_" + Guid.CreateVersion7().ToString("N");
            var correlation = Guid.NewGuid().ToString("N");
            var now = Now();
            Execute(connection, tx, "UPDATE dispatch_intents SET state='attempted' WHERE job_id=$id", ("$id", job.JobId));
            Execute(connection, tx, "INSERT INTO runs(run_id,job_id,generation,correlation,state,started_at) VALUES ($run,$id,1,$corr,'started',$now)",
                ("$run", runId), ("$id", job.JobId), ("$corr", correlation), ("$now", now));
            Execute(connection, tx, "UPDATE jobs SET status='running',session_id=$thread,updated_at=$now WHERE job_id=$id",
                ("$id", job.JobId), ("$thread", thread), ("$now", now));
            Execute(connection, tx, "INSERT INTO native_codex_attempts(job_id,thread_id,codex_home,correlation,state,created_at) VALUES ($id,$thread,$home,$corr,'sent',$now)",
                ("$id", job.JobId), ("$thread", thread), ("$home", codexHome), ("$corr", correlation), ("$now", now));
            Execute(connection, tx, "INSERT INTO events(job_id,run_id,kind,created_at) VALUES ($id,$run,'attempt_started',$now)",
                ("$id", job.JobId), ("$run", runId), ("$now", now));
            tx.Commit();
            return new AttemptClaim(GetJob(connection, null, job.JobId)!, runId, 1, correlation);
        }
        return null;
    });

    /// <summary>The child's bridge takes one durable offer. The posting fence commits before any socket write.</summary>
    public AttemptClaim? BeginNativeClaudeAttempt(string childJobId, string claudeHome, Func<string, bool> idle) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        var candidates = new List<JobRecord>();
        using (var command = Command(connection, tx, $"SELECT {JobColumns} FROM jobs j JOIN dispatch_intents i ON i.job_id=j.job_id WHERE i.state='unattempted' AND j.status='queued' AND (j.queue_deadline IS NULL OR j.queue_deadline>$now) AND instr(j.options,';native_claude=1')>0 ORDER BY i.created_at,i.rowid", ("$now", Now())))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) { candidates.Add(ReadJob(reader)); }
        }
        foreach (var job in candidates)
        {
            if (job.ParentJobId is not { } parentId || GetJob(connection, tx, parentId) is not { SessionId: { } session } parent
                || parent.Backend != "claude" || parent.Status is not (JobStatus.Completed or JobStatus.Cancelled)
                || SessionFenced(connection, tx, parentId)
                || Scalar(connection, tx, "SELECT count(*) FROM native_claude_attempts WHERE session_id=$session AND state IN ('posting','posted')", ("$session", session)) > 0
                || Scalar(connection, tx, "SELECT count(*) FROM native_codex_attempts WHERE thread_id=$session AND state='sent'", ("$session", session)) > 0)
            {
                continue;
            }
            // An earlier ordinary resume or native turn owns the session first.
            if (Scalar(connection, tx, """
                SELECT count(*) FROM jobs k LEFT JOIN jobs q ON q.job_id=k.parent_job_id
                WHERE k.job_id<>$id AND k.status IN ('queued','running')
                  AND (k.session_id=$session OR q.session_id=$session)
                  AND k.rowid < (SELECT rowid FROM jobs WHERE job_id=$id)
                """, ("$id", job.JobId), ("$session", session)) > 0)
            {
                continue;
            }
            var root = parent;
            while (root.ParentJobId is { } ancestor)
            {
                root = GetJob(connection, tx, ancestor)!;
            }
            if (root.JobId != childJobId || !idle(session)) { continue; }
            var runId = "run_" + Guid.CreateVersion7().ToString("N");
            var correlation = Guid.NewGuid().ToString("N");
            var now = Now();
            Execute(connection, tx, "UPDATE dispatch_intents SET state='attempted' WHERE job_id=$id", ("$id", job.JobId));
            Execute(connection, tx, "INSERT INTO runs(run_id,job_id,generation,correlation,state,started_at) VALUES ($run,$id,1,$corr,'started',$now)",
                ("$run", runId), ("$id", job.JobId), ("$corr", correlation), ("$now", now));
            Execute(connection, tx, "UPDATE jobs SET status='running',session_id=$session,updated_at=$now WHERE job_id=$id",
                ("$id", job.JobId), ("$session", session), ("$now", now));
            Execute(connection, tx, "INSERT INTO native_claude_attempts(job_id,child_job_id,session_id,claude_home,correlation,state,created_at) VALUES ($id,$child,$session,$home,$corr,'posting',$now)",
                ("$id", job.JobId), ("$child", childJobId), ("$session", session), ("$home", claudeHome), ("$corr", correlation), ("$now", now));
            Execute(connection, tx, "INSERT INTO events(job_id,run_id,kind,created_at) VALUES ($id,$run,'attempt_started',$now)",
                ("$id", job.JobId), ("$run", runId), ("$now", now));
            tx.Commit();
            return new AttemptClaim(GetJob(connection, null, job.JobId)!, runId, 1, correlation);
        }
        return null;
    });

    public NativeClaudeAttempt? NativeClaudeAttempt(string jobId) => Read(connection =>
    {
        using var command = Command(connection, null, "SELECT job_id,child_job_id,session_id,claude_home,correlation,state FROM native_claude_attempts WHERE job_id=$id", ("$id", jobId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new NativeClaudeAttempt(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)) : null;
    });

    public (string Address, string Secret, string Host)? ManagedClaudeChannel(string jobId) => Read<(string Address, string Secret, string Host)?>(connection =>
    {
        var root = GetJob(connection, null, jobId);
        while (root?.ParentJobId is { } parent) { root = GetJob(connection, null, parent); }
        if (root is null) { return null; }
        using var command = Command(connection, null, """
            SELECT t.address,t.secret,t.home FROM lead_sessions s JOIN wake_targets t ON t.target_key=s.wake_key
            WHERE s.binding_key=$binding AND s.closed_at IS NULL AND t.active=1 AND t.kind='claude'
            ORDER BY s.updated_at DESC LIMIT 1
            """, ("$binding", "managed-child:" + root.JobId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    });

    public IReadOnlyList<NativeClaudeAttempt> UnresolvedNativeClaudeAttempts() => Read(connection =>
    {
        using var command = Command(connection, null, "SELECT job_id,child_job_id,session_id,claude_home,correlation,state FROM native_claude_attempts WHERE state IN ('posting','posted','received')");
        using var reader = command.ExecuteReader();
        var rows = new List<NativeClaudeAttempt>();
        while (reader.Read()) { rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5))); }
        return rows;
    });

    public void RecordNativeClaudePost(string jobId, string correlation) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        Execute(connection, tx, "UPDATE native_claude_attempts SET state='posted' WHERE job_id=$id AND correlation=$corr AND state='posting'",
            ("$id", jobId), ("$corr", correlation));
        Execute(connection, tx, "UPDATE runs SET submitted_at=coalesce(submitted_at,$now) WHERE job_id=$id AND correlation=$corr",
            ("$id", jobId), ("$corr", correlation), ("$now", Now()));
        tx.Commit();
        return 0;
    });

    public void RecordNativeClaudeReceipt(string jobId, string correlation) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        if (Execute(connection, tx, "UPDATE native_claude_attempts SET state='received' WHERE job_id=$id AND correlation=$corr AND state IN ('posting','posted')",
            ("$id", jobId), ("$corr", correlation)) == 1)
        {
            Execute(connection, tx, "UPDATE runs SET acked=1,acknowledged_at=coalesce(acknowledged_at,$now) WHERE job_id=$id AND correlation=$corr",
                ("$id", jobId), ("$corr", correlation), ("$now", Now()));
        }
        tx.Commit();
        return 0;
    });

    public bool SettleNativeClaudeAttempt(string jobId, string correlation, string result) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        if (Execute(connection, tx, "UPDATE native_claude_attempts SET state='settled' WHERE job_id=$id AND correlation=$corr AND state IN ('posting','posted','received')",
            ("$id", jobId), ("$corr", correlation)) != 1) { return false; }
        var now = Now();
        Execute(connection, tx, "UPDATE runs SET state='completed',acked=1,acknowledged_at=coalesce(acknowledged_at,$now),finished_at=$now WHERE job_id=$id AND correlation=$corr",
            ("$id", jobId), ("$corr", correlation), ("$now", now));
        Execute(connection, tx, "UPDATE jobs SET status='completed',session_fenced=0,reason_code=NULL,result_text=$result,updated_at=$now WHERE job_id=$id",
            ("$id", jobId), ("$result", result), ("$now", now));
        Execute(connection, tx, "INSERT INTO events(job_id,kind,created_at) VALUES ($id,'completed',$now)", ("$id", jobId), ("$now", now));
        tx.Commit();
        return true;
    });

    /// <summary>Only a proven pre-write failure can return to the resume carrier.</summary>
    public bool RevertNativeClaudeAttempt(RunRef run) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        if (Execute(connection, tx, "UPDATE native_claude_attempts SET state='released' WHERE job_id=$id AND correlation=$corr AND state='posting'",
            ("$id", run.JobId), ("$corr", run.Correlation)) != 1) { return false; }
        var now = Now();
        Execute(connection, tx, "UPDATE runs SET state='failed',reason_code='native_not_started',finished_at=$now WHERE run_id=$run AND state='started'",
            ("$run", run.RunId), ("$now", now));
        Execute(connection, tx, "UPDATE jobs SET status='queued',session_id=NULL,options=replace(options,';native_claude=1',''),updated_at=$now WHERE job_id=$id AND status='running'",
            ("$id", run.JobId), ("$now", now));
        Execute(connection, tx, "UPDATE dispatch_intents SET state='unattempted' WHERE job_id=$id", ("$id", run.JobId));
        tx.Commit();
        return true;
    });

    /// <summary>The job's native attempt in any state, including a released one.</summary>
    public NativeCodexAttempt? NativeAttempt(string jobId) => Read(connection =>
    {
        using var command = Command(connection, null, "SELECT job_id,thread_id,codex_home,correlation,submission_id,state FROM native_codex_attempts WHERE job_id=$id", ("$id", jobId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new NativeCodexAttempt(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)) : null;
    });

    public string? NativeSubmissionId(string jobId) => Read(connection =>
    {
        using var command = Command(connection, null, "SELECT submission_id FROM native_codex_attempts WHERE job_id=$id", ("$id", jobId));
        return command.ExecuteScalar() as string;
    });

    public IReadOnlyList<NativeCodexAttempt> UnresolvedNativeAttempts() => Read(connection =>
    {
        using var command = Command(connection, null, "SELECT job_id,thread_id,codex_home,correlation,submission_id FROM native_codex_attempts WHERE state IN ('sent','received')");
        using var reader = command.ExecuteReader();
        var rows = new List<NativeCodexAttempt>();
        while (reader.Read()) { rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4))); }
        return (IReadOnlyList<NativeCodexAttempt>)rows;
    });

    public void RecordNativeSubmission(string jobId, string correlation, string submissionId) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        Execute(connection, tx, "UPDATE native_codex_attempts SET submission_id=coalesce(submission_id,$ref) WHERE job_id=$id AND correlation=$corr",
            ("$ref", submissionId), ("$id", jobId), ("$corr", correlation));
        tx.Commit();
        return 0;
    });

    /// <summary>The native user record proves presentation and releases N5.</summary>
    public void RecordNativeReceipt(string jobId, string correlation) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        if (Execute(connection, tx, "UPDATE native_codex_attempts SET state='received' WHERE job_id=$id AND correlation=$corr AND state='sent'",
            ("$id", jobId), ("$corr", correlation)) == 1)
        {
            Execute(connection, tx, "UPDATE runs SET acked=1,acknowledged_at=coalesce(acknowledged_at,$now) WHERE job_id=$id AND correlation=$corr",
                ("$id", jobId), ("$corr", correlation), ("$now", Now()));
        }
        tx.Commit();
        return 0;
    });

    public bool SettleNativeAttempt(string jobId, string correlation, string result) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        if (Execute(connection, tx, "UPDATE native_codex_attempts SET state='settled' WHERE job_id=$id AND correlation=$corr AND state IN ('sent','received')",
            ("$id", jobId), ("$corr", correlation)) != 1) { return false; }
        var now = Now();
        Execute(connection, tx, "UPDATE runs SET state='completed',acked=1,acknowledged_at=coalesce(acknowledged_at,$now),finished_at=$now WHERE job_id=$id AND correlation=$corr",
            ("$id", jobId), ("$corr", correlation), ("$now", now));
        Execute(connection, tx, "UPDATE jobs SET status='completed',session_fenced=0,reason_code=NULL,result_text=$result,updated_at=$now WHERE job_id=$id",
            ("$id", jobId), ("$result", result), ("$now", now));
        Execute(connection, tx, "INSERT INTO events(job_id,kind,created_at) VALUES ($id,'completed',$now)", ("$id", jobId), ("$now", now));
        tx.Commit();
        return true;
    });

    /// <summary>
    /// No codex process ever ran, so nothing can be presented: drop the fence and
    /// requeue the turn for the ordinary resume carrier.
    /// </summary>
    public bool RevertNativeAttempt(RunRef run) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        if (Execute(connection, tx, "UPDATE native_codex_attempts SET state='released' WHERE job_id=$id AND correlation=$corr AND state='sent'",
            ("$id", run.JobId), ("$corr", run.Correlation)) != 1) { return false; }
        var now = Now();
        if (Execute(connection, tx, "UPDATE runs SET state='failed',reason_code='native_not_started',finished_at=$now WHERE run_id=$run AND state='started'",
            ("$run", run.RunId), ("$now", now)) != 1
            || Execute(connection, tx, "UPDATE jobs SET status='queued',session_id=NULL,options=replace(options,';native_codex=1',''),updated_at=$now WHERE job_id=$id AND status='running'",
                ("$id", run.JobId), ("$now", now)) != 1)
        {
            return false;
        }
        Execute(connection, tx, """
            UPDATE dispatch_intents SET state='unattempted' WHERE job_id=$id;
            INSERT INTO events(job_id, run_id, kind, created_at) VALUES ($id, $run, 'native_reverted', $now);
            """, ("$id", run.JobId), ("$run", run.RunId), ("$now", now));
        tx.Commit();
        return true;
    });

    /// <summary>
    /// Operator escape from N5: an unresolved native attempt stops holding its
    /// thread and agent name. Its queued message may still be presented later.
    /// </summary>
    public CancelOutcome ReleaseNativeAttempt(string jobId, string principal, string team) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        var job = GetJob(connection, tx, jobId);
        if (job is null || job.Principal != principal || job.Team != team
            || Execute(connection, tx, "UPDATE native_codex_attempts SET state='released' WHERE job_id=$id AND state IN ('sent','received')", ("$id", jobId)) != 1)
        {
            return new CancelOutcome(job, false, false);
        }
        var now = Now();
        if (job.Status is JobStatus.Running or JobStatus.NeedsReconciliation)
        {
            Execute(connection, tx, "UPDATE runs SET state='cancelled', reason_code='stopped', finished_at=$now WHERE job_id=$id AND state IN ('started','needs_reconciliation')",
                ("$id", jobId), ("$now", now));
            Execute(connection, tx, "INSERT INTO events(job_id, kind, created_at) VALUES ($id, 'cancelled', $now)", ("$id", jobId), ("$now", now));
        }
        Execute(connection, tx, """
            UPDATE jobs SET status=CASE WHEN status IN ('running','needs_reconciliation') THEN 'cancelled' ELSE status END,
                reason_code=CASE WHEN status IN ('running','needs_reconciliation') THEN 'stopped' ELSE reason_code END,
                session_fenced=0, updated_at=$now WHERE job_id=$id;
            INSERT INTO events(job_id, kind, created_at) VALUES ($id, 'native_released', $now);
            """, ("$id", jobId), ("$now", now));
        CancelDeferredChildren(connection, tx, jobId);
        tx.Commit();
        return new CancelOutcome(GetJob(connection, null, jobId), false, true);
    });

    /// <summary>Diagnostic evidence only; never changes job state.</summary>
    public void RecordBackendEvidence(RunRef run, int? pid, bool acked) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        Execute(connection, tx, """
            UPDATE runs SET backend_pid=coalesce($pid, backend_pid), acked=max(acked, $acked), acknowledged_at=CASE WHEN $acked=1 THEN coalesce(acknowledged_at, $at) ELSE acknowledged_at END
            WHERE run_id=$run AND job_id=$id AND generation=$gen AND correlation=$corr AND state='started'
            """,
            ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$pid", pid), ("$acked", acked ? 1 : 0), ("$run", run.RunId), ("$id", run.JobId),
            ("$gen", run.Generation), ("$corr", run.Correlation));
        tx.Commit();
        return 0;
    });

    /// <summary>Records the backend's native session on the job, fenced to the current started run.</summary>
    public bool RecordSession(RunRef run, string sessionId) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        var updated = Execute(connection, tx, """
            UPDATE jobs SET session_id=$sid, updated_at=$now
            WHERE job_id=$id AND status='running' AND EXISTS (
                SELECT 1 FROM runs WHERE run_id=$run AND job_id=$id AND generation=$gen AND correlation=$corr AND state='started')
            """,
            ("$sid", sessionId), ("$now", Now()), ("$id", run.JobId), ("$run", run.RunId),
            ("$gen", run.Generation), ("$corr", run.Correlation));
        tx.Commit();
        return updated == 1;
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
    public bool EndUnsuccessfully(RunRef run, string terminalStatus, string reasonCode, string? message = null)
    {
        if (terminalStatus is not (JobStatus.Failed or JobStatus.NeedsReconciliation))
        {
            throw new ArgumentOutOfRangeException(nameof(terminalStatus));
        }

        return Finish(run, terminalStatus, terminalStatus, reasonCode, message, terminalStatus, null);
    }

    /// <summary>Atomically cancels queued or running work; terminal jobs are unchanged.</summary>
    public CancelOutcome Cancel(string jobId, string principal, string team, bool interrupt = false) => Cancel(jobId, principal, team, interrupt ? "interrupted" : "stopped");

    /// <summary>The daemon's own cancellation (a job timeout), through the same path as a stop.</summary>
    public CancelOutcome CancelOwned(string jobId, string reason) => Cancel(jobId, null, null, reason);

    CancelOutcome Cancel(string jobId, string? principal, string? team, string reason) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        var job = GetJob(connection, tx, jobId);
        if (job is null || (principal is not null && (job.Principal != principal || job.Team != team)))
        {
            return new CancelOutcome(null, false, false);
        }

        var running = job.Status == JobStatus.Running;
        var changed = CancelInTransaction(connection, tx, job, reason);
        // Only an operator stop ends the agent; an interrupt or a timeout keeps queued deferred turns.
        var childrenChanged = reason != "stopped" ? 0 : CancelDeferredChildren(connection, tx, jobId);
        if (changed || childrenChanged > 0)
        {
            tx.Commit();
            job = GetJob(connection, null, jobId)!;
        }

        return new CancelOutcome(job, running, changed);
    });

    /// <summary>Stops queued deferred turns of an idle agent before its session is closed.</summary>
    public void CancelDeferredChildren(string parentJobId) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        CancelDeferredChildren(connection, tx, parentJobId);
        tx.Commit();
        return 0;
    });

    static int CancelDeferredChildren(SqliteConnection connection, SqliteTransaction tx, string parentJobId)
    {
        var ids = new List<string>();
        using (var command = Command(connection, tx,
            "SELECT job_id FROM jobs WHERE parent_job_id=$id AND status='queued' AND instr(options, ';defer=1')>0", ("$id", parentJobId)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) { ids.Add(reader.GetString(0)); }
        }
        foreach (var id in ids)
        {
            CancelInTransaction(connection, tx, GetJob(connection, tx, id)!, "parent_stopped");
        }
        return ids.Count;
    }

    /// <summary>Records a verified stop of an uncertain run and releases its session fence.</summary>
    public CancelOutcome CancelReconciled(string jobId, string principal, string team) => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        var job = GetJob(connection, tx, jobId);
        if (job is null || job.Principal != principal || job.Team != team)
        {
            return new CancelOutcome(null, false, false);
        }
        if (job.Status != JobStatus.NeedsReconciliation)
        {
            return new CancelOutcome(job, false, false);
        }
        var now = Now();
        Execute(connection, tx, "UPDATE runs SET state='cancelled', reason_code='stopped', finished_at=$now WHERE job_id=$id AND generation=(SELECT max(generation) FROM runs WHERE job_id=$id)",
            ("$id", jobId), ("$now", now));
        Execute(connection, tx, """
            UPDATE jobs SET status='cancelled', session_fenced=0, reason_code='stopped', updated_at=$now WHERE job_id=$id;
            INSERT INTO events(job_id, kind, created_at) VALUES ($id, 'cancelled', $now);
            """, ("$id", jobId), ("$now", now));
        CancelDeferredChildren(connection, tx, jobId);
        tx.Commit();
        return new CancelOutcome(GetJob(connection, null, jobId), false, true);
    });

    static bool CancelInTransaction(SqliteConnection connection, SqliteTransaction tx, JobRecord job, string reason)
    {
        if (job.Status is not (JobStatus.Queued or JobStatus.Running))
        {
            return false;
        }

        var now = Now();
        if (job.Status == JobStatus.Running)
        {
            Execute(connection, tx, "UPDATE runs SET state='cancelled', reason_code=$reason, finished_at=$now WHERE job_id=$id AND state='started'",
                ("$id", job.JobId), ("$now", now), ("$reason", reason));
        }
        else
        {
            Execute(connection, tx, "UPDATE dispatch_intents SET state='attempted' WHERE job_id=$id AND state='unattempted'", ("$id", job.JobId));
        }

        Execute(connection, tx, """
            UPDATE jobs SET session_fenced=max(session_fenced, status='running'), status='cancelled', reason_code=$reason, updated_at=$now WHERE job_id=$id;
            INSERT INTO events(job_id, kind, created_at) VALUES ($id, 'cancelled', $now);
            """, ("$id", job.JobId), ("$now", now), ("$reason", reason));
        return true;
    }

    /// <summary>Cancels every queued job whose queue deadline has passed; returns their ids.</summary>
    public IReadOnlyList<string> ExpireQueued() => Write(connection =>
    {
        using var tx = connection.BeginTransaction(deferred: false);
        var now = Now();
        var expired = new List<string>();
        using (var select = Command(connection, tx,
            "SELECT job_id FROM jobs WHERE status='queued' AND queue_deadline IS NOT NULL AND queue_deadline <= $now", ("$now", now)))
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                expired.Add(reader.GetString(0));
            }
        }

        foreach (var jobId in expired)
        {
            Execute(connection, tx, """
                UPDATE dispatch_intents SET state='attempted' WHERE job_id=$id AND state='unattempted';
                UPDATE jobs SET status='cancelled', reason_code='queue_ttl', updated_at=$now WHERE job_id=$id;
                INSERT INTO events(job_id, kind, created_at) VALUES ($id, 'cancelled', $now);
                """, ("$id", jobId), ("$now", now));
        }

        tx.Commit();
        return (IReadOnlyList<string>)expired;
    });

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
                UPDATE jobs SET status=$status, session_fenced=max(session_fenced, $status='needs_reconciliation'), reason_code=$reason, result_text=$result, updated_at=$now
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
                UPDATE jobs SET status='needs_reconciliation', session_fenced=1, reason_code='daemon_restart_uncertain', updated_at=$now
                WHERE job_id=$id AND status='running';
                INSERT INTO events(job_id, kind, created_at) VALUES ($id, 'needs_reconciliation', $now);
                """,
                ("$id", jobId), ("$now", now));
        }

        tx.Commit();
        return (IReadOnlyList<string>)jobs;
    });

    /// <summary>
    /// Run markers whose backend processes may have outlived daemon death,
    /// including runs that died before their PID was recorded.
    /// </summary>
    public IReadOnlyList<string> GetInterruptedRunCorrelations() => Read(connection =>
    {
        using var command = Command(connection, null, """
            SELECT correlation FROM runs
            WHERE state IN ('started','cancelled') OR (state='needs_reconciliation' AND reason_code='daemon_restart_uncertain')
            """);
        using var reader = command.ExecuteReader();
        var correlations = new List<string>();
        while (reader.Read())
        {
            correlations.Add(reader.GetString(0));
        }

        return (IReadOnlyList<string>)correlations;
    });

    public JobRecord? GetJob(string jobId) => Read(connection => GetJob(connection, null, jobId));

    /// <summary>Used after an interrupt to catch a queued child stopped before Esc finished.</summary>
    public bool HasQueuedFollowUp(string parentJobId) => Read(connection =>
        Scalar(connection, null, "SELECT count(*) FROM jobs WHERE parent_job_id=$id AND status='queued'", ("$id", parentJobId)) > 0);

    /// <summary>Newest first, scoped to one principal/team.</summary>
    public IReadOnlyList<JobRecord> ListJobs(string principal, string team, int limit) => Read(connection =>
    {
        using var command = Command(connection, null,
            $"SELECT {JobColumns} FROM jobs j WHERE j.principal=$p AND j.team=$t ORDER BY j.accepted_at DESC, j.rowid DESC LIMIT $n",
            ("$p", principal), ("$t", team), ("$n", limit));
        using var reader = command.ExecuteReader();
        var jobs = new List<JobRecord>();
        while (reader.Read())
        {
            jobs.Add(ReadJob(reader));
        }

        return (IReadOnlyList<JobRecord>)jobs;
    });

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

    public void RecordStartup(RunRef run, string phase) => Write(connection =>
    {
        var column = phase switch { "ready" => "ready_at", "submitted" => "submitted_at", _ => throw new ArgumentOutOfRangeException(nameof(phase)) };
        using var tx = connection.BeginTransaction(deferred: false);
        Execute(connection, tx, $"UPDATE runs SET {column}=coalesce({column}, $at) WHERE run_id=$run AND job_id=$id AND generation=$gen AND correlation=$corr AND state='started'",
            ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$run", run.RunId), ("$id", run.JobId), ("$gen", run.Generation), ("$corr", run.Correlation));
        tx.Commit();
        return 0;
    });

    public IReadOnlyList<RunRecord> GetRuns(string jobId) => Read(connection =>
    {
        using var command = Command(connection, null,
            "SELECT run_id, generation, correlation, state, acked, backend_pid, reason_code, started_at, ready_at, submitted_at, acknowledged_at, finished_at FROM runs WHERE job_id=$id ORDER BY generation", ("$id", jobId));
        using var reader = command.ExecuteReader();
        var runs = new List<RunRecord>();
        while (reader.Read())
        {
            runs.Add(new RunRecord(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4) != 0, reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6))
            {
                StartedAt = reader.GetString(7),
                ReadyAt = reader.IsDBNull(8) ? null : reader.GetString(8),
                SubmittedAt = reader.IsDBNull(9) ? null : reader.GetString(9),
                AcknowledgedAt = reader.IsDBNull(10) ? null : reader.GetString(10),
                FinishedAt = reader.IsDBNull(11) ? null : reader.GetString(11),
            });
        }

        return (IReadOnlyList<RunRecord>)runs;
    });

    /// <summary>
    /// One output-bounded keyset page of a principal/team's jobs, newest job ID first
    /// by default, or newest activity first for the web overview.
    /// A single read-only statement: it takes no write lock and changes no row,
    /// intent, event or cursor. Each call is an independent committed read (live
    /// keyset, not a snapshot). No `(principal, team, job_id)` index exists, so DB
    /// work still grows with the caller's total jobs; only the returned rows are capped.
    /// </summary>
    public IReadOnlyList<JobSummaryRecord> ListJobs(string principal, string team, string? status, string? backend, string? since, string? beforeJobId, int take,
        string? leadSessionId = null, string? workspace = null, bool orderByActivity = false, bool includeConnector = false) => Read(connection =>
    {
        using var command = Command(connection, null, """
            SELECT j.job_id, j.status, j.reason_code, (SELECT count(*) FROM runs r WHERE r.job_id = j.job_id), j.accepted_at, j.updated_at, j.worktree_path, j.worktree_branch,
                   j.backend, j.session_id, j.parent_job_id, j.lead_session_id, j.target_agent, j.options,
                   (SELECT s.workspace FROM lead_sessions s WHERE s.session_id=j.lead_session_id AND s.closed_at IS NULL), j.cwd, j.principal='prfactory' AND j.team='connector'
            FROM jobs j
            WHERE ((j.principal=$p AND j.team=$t) OR ($connector=1 AND j.principal='prfactory' AND j.team='connector'))
              -- A malformed pre-release row must not break this page or its cursor.
              AND typeof(j.job_id)='text' AND typeof(j.status)='text'
              AND typeof(j.accepted_at)='text' AND typeof(j.updated_at)='text'
              AND typeof(j.backend)='text' AND typeof(j.target_agent)='text'
              AND typeof(j.options)='text'
              AND ($lead IS NULL OR j.lead_session_id=$lead OR ($workspace IS NOT NULL AND j.lead_session_id IN (SELECT session_id FROM lead_sessions WHERE workspace=$workspace)))
              AND ($status IS NULL OR j.status=$status)
              AND ($backend IS NULL OR j.backend=$backend)
              AND ($since IS NULL OR j.accepted_at >= $since)
              AND ($before IS NULL OR ($activity=0 AND j.job_id < $before)
                OR ($activity=1 AND (j.updated_at < (SELECT updated_at FROM jobs WHERE job_id=$before)
                  OR (j.updated_at = (SELECT updated_at FROM jobs WHERE job_id=$before) AND j.job_id < $before))))
            ORDER BY CASE WHEN $activity=1 THEN j.updated_at ELSE j.job_id END DESC, j.job_id DESC
            LIMIT $take
            """,
            ("$p", principal), ("$t", team), ("$connector", includeConnector ? 1 : 0), ("$lead", leadSessionId), ("$workspace", workspace), ("$status", status), ("$backend", backend), ("$since", since), ("$before", beforeJobId), ("$activity", orderByActivity ? 1 : 0), ("$take", take));
        using var reader = command.ExecuteReader();
        var jobs = new List<JobSummaryRecord>();
        while (reader.Read())
        {
            jobs.Add(new JobSummaryRecord(reader.GetString(0), reader.GetString(1), NullableText(reader, 2),
                reader.GetInt32(3), reader.GetString(4), reader.GetString(5))
            {
                WorktreePath = NullableText(reader, 6),
                WorktreeBranch = NullableText(reader, 7),
                Backend = reader.GetString(8),
                SessionId = NullableText(reader, 9),
                ParentJobId = NullableText(reader, 10),
                LeadSessionId = NullableText(reader, 11),
                TargetAgent = reader.GetString(12),
                Options = NullableText(reader, 13),
                LeadWorkspace = NullableText(reader, 14),
                Cwd = NullableText(reader, 15),
                Connector = reader.GetBoolean(16),
            });
        }

        return (IReadOnlyList<JobSummaryRecord>)jobs;
    });

    /// <summary>
    /// Whether a lead may address this job by id: its own jobs and unscoped (CLI,
    /// connector or pre-session) jobs always; with a workspace, also sibling leads'
    /// jobs there, so jobs shown by list_jobs(all_workspace) can be inspected.
    /// </summary>
    public bool LeadCanAccess(string jobId, string leadSessionId, string? workspace) => Read(connection =>
        Scalar(connection, null, """
            SELECT count(*) FROM jobs WHERE job_id=$id AND (lead_session_id IS NULL OR lead_session_id=$lead
                OR ($workspace IS NOT NULL AND lead_session_id IN (SELECT session_id FROM lead_sessions WHERE workspace=$workspace)))
            """, ("$id", jobId), ("$lead", leadSessionId), ("$workspace", workspace)) == 1);

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

        return (ReadJob(reader), reader.GetString(19));
    }

    static JobRecord ReadJob(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.GetString(7),
        NullableString(reader, 8), NullableString(reader, 9), reader.GetInt32(10),
        reader.GetString(11), NullableString(reader, 12), NullableString(reader, 13), NullableString(reader, 14))
    {
        WorktreePath = NullableString(reader, 15),
        WorktreeBranch = NullableString(reader, 16),
        WorktreeBase = NullableString(reader, 17),
        TimeoutSeconds = reader.IsDBNull(18) ? null : reader.GetInt32(18),
    };

    static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    static string? NullableText(SqliteDataReader reader, int ordinal) => !reader.IsDBNull(ordinal) && reader.GetFieldType(ordinal) == typeof(string)
        ? reader.GetString(ordinal) : null;

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
