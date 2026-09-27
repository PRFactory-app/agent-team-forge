using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.DAL.Features.Wake;

public sealed record WakeRegistration(string Key, long Generation, string Kind, string Address, string Secret, string Home);
public sealed record WakeRegistrationStatus(bool Registered, string? Key, long? Generation, string? Kind, string? Address, bool Usable = false);
public sealed record WakeSnapshot(WakeRegistration Target, int Unread, long LatestSeq, long NotifiedSeq, DateTimeOffset? LastSuccess, bool Outstanding, bool External = false);

/// <summary>Committed wake routing and unread state. A posted notice is only a doorbell, never a read receipt.</summary>
public sealed class WakeStore(JobDatabase database)
{
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

    public void MarkRead(string jobId, string key, long generation)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE wake_jobs SET read_at=$now WHERE job_id=$job AND target_key=$key AND read_at IS NULL
            AND EXISTS (SELECT 1 FROM wake_targets WHERE target_key=$key AND generation=$generation AND active=1)
            AND EXISTS (SELECT 1 FROM jobs WHERE job_id=$job AND status IN ('completed','failed','needs_reconciliation','cancelled'));
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$generation", generation);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<WakeSnapshot> Pending()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.target_key, t.generation, t.kind, t.address, t.secret, t.home,
                   count(DISTINCT w.job_id), coalesce(max(e.seq),0), t.notified_seq, t.last_success,
                   sum(CASE WHEN e.seq <= t.notified_seq THEN 1 ELSE 0 END)
            FROM wake_targets t
            JOIN wake_jobs w ON w.target_key=t.target_key AND w.read_at IS NULL
            JOIN jobs j ON j.job_id=w.job_id AND j.status IN ('completed','failed','needs_reconciliation','cancelled')
            JOIN events e ON e.job_id=j.job_id AND e.kind IN ('completed','failed','needs_reconciliation','cancelled')
            WHERE t.active=1 GROUP BY t.target_key;
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
                   sum(CASE WHEN m.seq<=t.external_notified_seq THEN 1 ELSE 0 END)
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
                reader.GetInt64(10) > 0, true));
        }
        return result;
    }

    public bool MarkNotified(WakeSnapshot snapshot, DateTimeOffset now)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = snapshot.External ? """
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
