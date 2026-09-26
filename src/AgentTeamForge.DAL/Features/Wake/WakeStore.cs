using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.DAL.Features.Wake;

public sealed record WakeRegistration(string Key, long Generation, string Kind, string Address, string Secret, string Home);
public sealed record WakeSnapshot(WakeRegistration Target, int Unread, long LatestSeq, long NotifiedSeq, DateTimeOffset? LastSuccess, bool Outstanding);

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
                generation=generation+1, kind=excluded.kind, address=excluded.address,
                secret=excluded.secret, home=excluded.home, registered_at=excluded.registered_at,
                notified_seq=0, last_success=NULL;
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

    public void MarkRead(string jobId, string key, long generation)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE wake_jobs SET read_at=$now WHERE job_id=$job AND target_key=$key AND read_at IS NULL
            AND EXISTS (SELECT 1 FROM wake_targets WHERE target_key=$key AND generation=$generation)
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

    public bool MarkNotified(WakeSnapshot snapshot, DateTimeOffset now)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE wake_targets SET notified_seq=max(notified_seq,$seq), last_success=$now
            WHERE target_key=$key AND generation=$generation;
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
        command.CommandText = "SELECT count(*) FROM wake_targets WHERE target_key=$key AND generation=$generation";
        command.Parameters.AddWithValue("$key", target.Key);
        command.Parameters.AddWithValue("$generation", target.Generation);
        return (long)command.ExecuteScalar()! == 1;
    }
}
