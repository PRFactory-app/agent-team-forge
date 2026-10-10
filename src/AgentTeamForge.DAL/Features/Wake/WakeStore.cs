using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.DAL.Features.Wake;

public sealed record WakeRegistration(string Key, long Generation, string Kind, string Address, string Secret, string Home);
public sealed record WakeRegistrationStatus(bool Registered, string? Key, long? Generation, string? Kind, string? Address, bool Usable = false);
public sealed record WakeSnapshot(WakeRegistration Target, int Unread, long LatestSeq, long NotifiedSeq, DateTimeOffset? LastSuccess, bool Outstanding, bool External = false,
    string? ParkJobId = null, string? ReaderId = null, bool ReadSincePost = false);

/// <summary>Committed wake routing and unread state. A posted notice is only a doorbell, never a read receipt.</summary>
public sealed class WakeStore(JobDatabase database)
{
    // Uses job alias j and inbox message alias m. Retention must preserve the same evidence the wake scan uses.
    internal const string CompletionReportPredicate = """
        m.team_id=j.lead_session_id AND m.recipient='lead'
        -- A report the lead consumed before the job finished cannot announce the completion.
        AND (m.read_at IS NULL OR m.read_at >= j.updated_at)
        AND m.created_at >= (SELECT max(r.started_at) FROM runs r WHERE r.job_id=j.job_id)
        AND m.created_at <= j.updated_at
        AND m.sender IN (
            WITH RECURSIVE ancestors(id,parent) AS (
                SELECT j.job_id,j.parent_job_id
                UNION ALL
                SELECT p.job_id,p.parent_job_id FROM jobs p JOIN ancestors a ON p.job_id=a.parent
            ) SELECT 'child-'||id FROM ancestors)
        """;

    public WakeRegistration Register(string key, string kind, string address, string secret, string home)
    {
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO wake_targets(target_key, generation, kind, address, secret, home, registered_at)
            VALUES ($key, 1, $kind, $address, $secret, $home, $now)
            ON CONFLICT(target_key) DO UPDATE SET
                generation=generation+1, active=1, kind=excluded.kind, address=excluded.address,
                secret=excluded.secret, home=excluded.home, registered_at=excluded.registered_at,
                notified_seq=0, last_success=NULL, external_notified_seq=0, last_external_success=NULL;
            SELECT generation FROM wake_targets WHERE target_key=$key;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$address", address);
        command.Parameters.AddWithValue("$secret", secret);
        command.Parameters.AddWithValue("$home", home);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        var generation = (long)command.ExecuteScalar()!;
        tx.Commit();
        return new WakeRegistration(key, generation, kind, address, secret, home);
    }

    public WakeRegistrationStatus Status(string sessionId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.target_key,t.generation,t.kind,t.address,
                   CASE WHEN t.kind='claude' THEN length(t.secret)>0 AND length(t.home)>0 ELSE 1 END
            FROM lead_sessions s
            JOIN wake_targets t ON t.target_key=s.wake_key AND t.active=1
            WHERE s.session_id=$id AND s.closed_at IS NULL
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(true, reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4) != 0)
            : new(false, null, null, null, null);
    }

    public bool ClearLead(string sessionId, string key, long generation)
    {
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE wake_targets SET active=0,generation=generation+1,notified_seq=0,last_success=NULL,
                external_notified_seq=0,last_external_success=NULL
            WHERE target_key=$key AND generation=$generation AND active=1
            AND EXISTS (SELECT 1 FROM lead_sessions WHERE session_id=$id AND wake_key=$key AND closed_at IS NULL);
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$id", sessionId);
        if ((long)command.ExecuteScalar()! != 1) { return false; }
        command.CommandText = """
            UPDATE lead_sessions SET wake_key=NULL WHERE wake_key=$key;
            UPDATE external_teams SET wake_key=NULL WHERE wake_key=$key;
            UPDATE external_messages SET wake_key=NULL WHERE wake_key=$key AND read_at IS NULL;
            """;
        command.ExecuteNonQuery();
        tx.Commit();
        return true;
    }

    public void Invalidate(string key)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE wake_targets SET active=0,generation=generation+1,notified_seq=0,last_success=NULL,
                external_notified_seq=0,last_external_success=NULL
            WHERE target_key=$key AND active=1
            """;
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    /// <summary>Deactivates Claude targets whose owner is provably <paramref name="gone"/>, once nothing registered
    /// or posted to them for 10 minutes. Open lead sessions are resumable even when their managed job has
    /// completed or their bridge is disconnected, so their targets are never pruned. Unread rows stay;
    /// resume_session rebinds them to a live target.</summary>
    public IReadOnlyList<string> PruneDead(DateTimeOffset now, Func<WakeRegistration, bool> gone)
    {
        using var connection = database.OpenConnection();
        var candidates = new List<(WakeRegistration Target, DateTimeOffset LastOk)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT t.target_key,t.generation,t.kind,t.address,t.secret,t.home,
                       t.registered_at,t.last_success,t.last_external_success
                FROM wake_targets t WHERE t.active=1 AND t.kind='claude'
                """;
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var target = new WakeRegistration(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
                var lastOk = Enumerable.Range(6, 3).Where(i => !reader.IsDBNull(i))
                    .Max(i => DateTimeOffset.Parse(reader.GetString(i), System.Globalization.CultureInfo.InvariantCulture));
                candidates.Add((target, lastOk));
            }
        }
        var pruned = new List<string>();
        foreach (var (target, lastOk) in candidates)
        {
            if (now - lastOk <= TimeSpan.FromMinutes(10) || !gone(target)) { continue; }
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE wake_targets SET active=0,generation=generation+1,notified_seq=0,last_success=NULL,
                    external_notified_seq=0,last_external_success=NULL
                WHERE target_key=$key AND generation=$generation AND active=1
                AND NOT EXISTS (SELECT 1 FROM lead_sessions s WHERE s.wake_key=$key AND s.closed_at IS NULL)
                """;
            update.Parameters.AddWithValue("$key", target.Key);
            update.Parameters.AddWithValue("$generation", target.Generation);
            if (update.ExecuteNonQuery() == 1) { pruned.Add(target.Key); }
        }
        return pruned;
    }

    /// <summary>Reads only the terminal state (status and event revision) the caller saw; a null revision skips the revision check: a completion racing a read of the running job still wakes.</summary>
    public void MarkRead(string jobId, string observedStatus, string key, long generation, long? revision = null)
    {
        if (observedStatus is Jobs.JobStatus.Queued or Jobs.JobStatus.Running) { return; }
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE wake_jobs SET read_at=$now WHERE job_id=$job AND target_key=$key AND read_at IS NULL
            AND EXISTS (SELECT 1 FROM wake_targets WHERE target_key=$key AND generation=$generation AND active=1)
            AND EXISTS (SELECT 1 FROM jobs j WHERE j.job_id=$job AND j.status=$status
                AND ($revision IS NULL OR (SELECT coalesce(max(e.seq),0) FROM events e WHERE e.job_id=j.job_id)=$revision));
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$status", observedStatus);
        command.Parameters.AddWithValue("$revision", (object?)revision ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$generation", generation);
        command.ExecuteNonQuery();
    }

    /// <summary>Acks by owning lead session, so a read while the wake is cleared or not yet re-registered still counts.</summary>
    public void MarkReadForLead(string jobId, string observedStatus, long? revision, string leadSessionId)
    {
        if (observedStatus is Jobs.JobStatus.Queued or Jobs.JobStatus.Running) { return; }
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE wake_jobs SET read_at=$now WHERE job_id=$job AND read_at IS NULL
            AND EXISTS (SELECT 1 FROM jobs j WHERE j.job_id=$job AND j.lead_session_id=$lead AND j.status=$status
                AND ($revision IS NULL OR (SELECT coalesce(max(e.seq),0) FROM events e WHERE e.job_id=j.job_id)=$revision));
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$status", observedStatus);
        command.Parameters.AddWithValue("$revision", (object?)revision ?? DBNull.Value);
        command.Parameters.AddWithValue("$lead", leadSessionId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<WakeSnapshot> Pending()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT t.target_key, t.generation, t.kind, t.address, t.secret, t.home,
                   count(DISTINCT w.job_id), coalesce(max(e.seq),0), t.notified_seq, t.last_success,
                   sum(CASE WHEN e.seq <= t.notified_seq THEN 1 ELSE 0 END)
            FROM wake_targets t
            JOIN wake_jobs w ON w.target_key=t.target_key AND w.read_at IS NULL
            JOIN jobs j ON j.job_id=w.job_id AND j.status IN ('completed','failed','needs_reconciliation','cancelled')
                -- Session-owned parks wake once through PendingParks; session-less ones keep the terminal notice.
                AND (j.lead_session_id IS NULL OR j.reason_code IS NULL OR j.reason_code!='interactive_completion_unobserved')
            JOIN events e ON e.job_id=j.job_id AND e.kind IN ('completed','failed','needs_reconciliation','cancelled')
            WHERE t.active=1
              -- A managed child's committed report is already a doorbell for this turn.
              -- Keep failures/attention notices and unread job state independent of that report.
              AND (j.status!='completed' OR NOT EXISTS (
                  SELECT 1 FROM external_messages m
                  WHERE {CompletionReportPredicate}))
            GROUP BY t.target_key;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<WakeSnapshot>();
        while (reader.Read())
        {
            var target = new WakeRegistration(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
            result.Add(new WakeSnapshot(target, reader.GetInt32(6), reader.GetInt64(7), reader.GetInt64(8),
                reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetInt64(10) > 0));
        }
        return result;
    }

    public IReadOnlyList<WakeSnapshot> PendingExternal()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.target_key,t.generation,t.kind,t.address,t.secret,t.home,
                   count(m.seq),max(m.seq),t.external_notified_seq,t.last_external_success,
                   sum(CASE WHEN m.seq<=t.external_notified_seq THEN 1 ELSE 0 END),
                   -- Read evidence includes already-read rows: any read since the last post means the notice was consumed.
                   EXISTS (SELECT 1 FROM external_messages r WHERE r.wake_key=t.target_key AND r.read_at IS NOT NULL
                           AND r.read_at>=t.last_external_success)
            FROM wake_targets t JOIN external_messages m ON m.wake_key=t.target_key AND m.read_at IS NULL
            WHERE t.active=1
            GROUP BY t.target_key
            """;
        using var reader = command.ExecuteReader();
        var result = new List<WakeSnapshot>();
        while (reader.Read())
        {
            var target = new WakeRegistration(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
            result.Add(new WakeSnapshot(target, reader.GetInt32(6), reader.GetInt64(7), reader.GetInt64(8),
                reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetInt64(10) > 0, true, ReadSincePost: reader.GetInt64(11) > 0));
        }
        return result;
    }

    public IReadOnlyList<WakeSnapshot> PendingParks()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.target_key,t.generation,t.kind,t.address,t.secret,t.home,s.session_id,j.job_id
            FROM jobs j
            JOIN lead_sessions s ON s.session_id=j.lead_session_id AND s.closed_at IS NULL
            JOIN wake_targets t ON t.target_key=s.wake_key AND t.active=1
            WHERE j.status='needs_reconciliation' AND j.reason_code='interactive_completion_unobserved'
              AND NOT EXISTS (SELECT 1 FROM wake_park_acks a WHERE a.reader_id=s.session_id AND a.job_id=j.job_id)
            """;
        using var reader = command.ExecuteReader();
        var result = new List<WakeSnapshot>();
        while (reader.Read())
        {
            var target = new WakeRegistration(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
            result.Add(new WakeSnapshot(target, 1, 1, 0, null, false, ParkJobId: reader.GetString(7), ReaderId: reader.GetString(6)));
        }
        return result;
    }

    public bool MarkNotified(WakeSnapshot snapshot, DateTimeOffset now)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = snapshot.ParkJobId is not null ? """
            INSERT OR IGNORE INTO wake_park_acks(reader_id,job_id,notified_at)
            SELECT $reader,$job,$now WHERE EXISTS (
                SELECT 1 FROM jobs j JOIN lead_sessions s ON s.session_id=j.lead_session_id
                JOIN wake_targets t ON t.target_key=s.wake_key
                WHERE j.job_id=$job AND s.session_id=$reader AND s.closed_at IS NULL
                  AND j.status='needs_reconciliation' AND j.reason_code='interactive_completion_unobserved'
                  AND t.target_key=$key AND t.generation=$generation AND t.active=1);
            """ : snapshot.External ? """
            UPDATE wake_targets SET external_notified_seq=max(external_notified_seq,$seq), last_external_success=$now
            WHERE target_key=$key AND generation=$generation AND active=1;
            """ : """
            UPDATE wake_targets SET notified_seq=max(notified_seq,$seq), last_success=$now
            WHERE target_key=$key AND generation=$generation AND active=1;
            """;
        command.Parameters.AddWithValue("$seq", snapshot.LatestSeq);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$key", snapshot.Target.Key);
        command.Parameters.AddWithValue("$generation", snapshot.Target.Generation);
        if (snapshot.ParkJobId is not null)
        {
            command.Parameters.AddWithValue("$job", snapshot.ParkJobId);
            command.Parameters.AddWithValue("$reader", snapshot.ReaderId!);
        }
        return command.ExecuteNonQuery() == 1;
    }

    public bool IsCurrent(WakeRegistration target)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM wake_targets WHERE target_key=$key AND generation=$generation AND active=1";
        command.Parameters.AddWithValue("$key", target.Key);
        command.Parameters.AddWithValue("$generation", target.Generation);
        return (long)command.ExecuteScalar()! == 1;
    }
}
