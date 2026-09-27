using AgentTeamForge.DAL.Sqlite;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.DAL.Features.Jobs;

public sealed record AccountPark(string JobId, string Backend, string AccountKey, string Reason,
    string? SessionId, DateTimeOffset ObservedAt, DateTimeOffset? ResetsAt, string State);

/// <summary>Durable account exhaustion and same-lineage park state. A missing/corrupt schema fails closed.</summary>
public sealed class AccountWindowStore(JobDatabase database)
{
    const string UpsertWindowSql = """
        INSERT INTO account_windows(backend, account_key, reason, observed_at, resets_at)
        VALUES($backend,$account,$reason,$observed,$reset)
        ON CONFLICT(backend,account_key) DO UPDATE SET reason=excluded.reason,
            observed_at=excluded.observed_at,
            resets_at=CASE
                WHEN account_windows.resets_at IS NULL OR excluded.resets_at IS NULL THEN NULL
                WHEN account_windows.resets_at > excluded.resets_at THEN account_windows.resets_at
                ELSE excluded.resets_at END
        """;

    public void Block(string backend, string accountKey, string reason, DateTimeOffset observedAt, DateTimeOffset? resetsAt)
    {
        using var connection = database.OpenConnection();
        using var command = Command(connection, null, UpsertWindowSql, backend, accountKey, reason, observedAt, resetsAt);
        command.ExecuteNonQuery();
    }

    public void Park(string jobId, string backend, string accountKey, string reason, string? sessionId,
        DateTimeOffset observedAt, DateTimeOffset? resetsAt)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var window = Command(connection, transaction, UpsertWindowSql, backend, accountKey, reason, observedAt, resetsAt))
        {
            window.ExecuteNonQuery();
        }
        using (var park = Command(connection, transaction, """
            INSERT INTO account_parks(job_id,backend,account_key,reason,session_id,observed_at,resets_at)
            VALUES($job,$backend,$account,$reason,$session,$observed,$reset)
            ON CONFLICT(job_id) DO UPDATE SET reason=excluded.reason, observed_at=excluded.observed_at,
                resets_at=excluded.resets_at, state='parked'
            WHERE account_parks.backend=excluded.backend AND account_parks.account_key=excluded.account_key
                AND account_parks.session_id IS excluded.session_id
            """, backend, accountKey, reason, observedAt, resetsAt, jobId, sessionId))
        {
            if (park.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("Park identity changed; recovery requires operator action");
            }
        }
        transaction.Commit();
    }

    public bool IsBlocked(string backend, string accountKey, DateTimeOffset now)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT resets_at FROM account_windows WHERE backend=$backend AND account_key=$account";
        command.Parameters.AddWithValue("$backend", backend);
        command.Parameters.AddWithValue("$account", accountKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { return false; }
        return reader.IsDBNull(0) || ParseDate(reader.GetString(0)) > now;
    }

    public AccountPark? GetPark(string jobId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT backend,account_key,reason,session_id,observed_at,resets_at,state FROM account_parks WHERE job_id=$job";
        command.Parameters.AddWithValue("$job", jobId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new AccountPark(jobId, reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), ParseDate(reader.GetString(4)),
            reader.IsDBNull(5) ? null : ParseDate(reader.GetString(5)), reader.GetString(6)) : null;
    }

    /// <summary>Reserve one due park for a resume attempt. The caller starts the saved job/session then marks it resumed.</summary>
    public IReadOnlyList<AccountPark> Due(DateTimeOffset now)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.job_id FROM account_parks p JOIN account_windows w
              ON w.backend=p.backend AND w.account_key=p.account_key
            WHERE p.state='parked' AND p.resets_at IS NOT NULL AND p.resets_at <= $now
              AND w.resets_at IS NOT NULL AND w.resets_at <= $now ORDER BY p.resets_at
            """;
        command.Parameters.AddWithValue("$now", Stamp(now));
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) { ids.Add(reader.GetString(0)); }
        return [.. ids.Select(GetPark).OfType<AccountPark>()];
    }

    /// <summary>After a crash, reconcile these with the durable resumed turn before retrying.</summary>
    public IReadOnlyList<AccountPark> Resuming()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT job_id FROM account_parks WHERE state='resuming'";
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) { ids.Add(reader.GetString(0)); }
        return [.. ids.Select(GetPark).OfType<AccountPark>()];
    }

    /// <summary>Explicit operator recovery for a known account; never guesses an unknown reset time.</summary>
    public void PermitAccountRecovery(string backend, string accountKey, DateTimeOffset now)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE account_windows SET resets_at=$now WHERE backend=$backend AND account_key=$account";
        command.Parameters.AddWithValue("$backend", backend);
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$now", Stamp(now));
        if (command.ExecuteNonQuery() != 1) { throw new InvalidOperationException("Account window is not recoverable"); }
        command.CommandText = "UPDATE account_parks SET resets_at=$now WHERE backend=$backend AND account_key=$account AND state='parked'";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public bool MarkResuming(string jobId, DateTimeOffset now)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE account_parks SET state='resuming' WHERE job_id=$job AND state='parked'
            AND resets_at IS NOT NULL AND resets_at <= $now
            AND EXISTS (SELECT 1 FROM account_windows w WHERE w.backend=account_parks.backend
                AND w.account_key=account_parks.account_key AND w.resets_at IS NOT NULL
                AND w.resets_at <= $now)
            """;
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$now", Stamp(now));
        return command.ExecuteNonQuery() == 1;
    }

    public void MarkResumed(string jobId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE account_parks SET state='resumed' WHERE job_id=$job AND state='resuming'";
        command.Parameters.AddWithValue("$job", jobId);
        if (command.ExecuteNonQuery() != 1) { throw new InvalidOperationException("Park is not resuming"); }
    }

    public void RetryResume(string jobId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE account_parks SET state='parked' WHERE job_id=$job AND state='resuming'";
        command.Parameters.AddWithValue("$job", jobId);
        command.ExecuteNonQuery();
    }

    /// <summary>Transactionally limits accepted teams plus unconsumed poll reservations across callers.</summary>
    public string? TryReserveTeam(int maxAccepted, DateTimeOffset now, TimeSpan lifetime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAccepted, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var stale = connection.CreateCommand())
        {
            stale.Transaction = transaction;
            stale.CommandText = "DELETE FROM account_admission_reservations WHERE expires_at <= $now";
            stale.Parameters.AddWithValue("$now", Stamp(now));
            stale.ExecuteNonQuery();
        }
        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT (" + PRFactoryTeamStore.AdmissionCountSql
                + ") + (SELECT count(*) FROM account_admission_reservations)";
            if ((long)count.ExecuteScalar()! >= maxAccepted) { transaction.Commit(); return null; }
        }
        var token = Guid.NewGuid().ToString("N");
        using (var reserve = connection.CreateCommand())
        {
            reserve.Transaction = transaction;
            reserve.CommandText = "INSERT INTO account_admission_reservations(token,expires_at) VALUES($token,$expires)";
            reserve.Parameters.AddWithValue("$token", token);
            reserve.Parameters.AddWithValue("$expires", Stamp(now.Add(lifetime)));
            reserve.ExecuteNonQuery();
        }
        transaction.Commit();
        return token;
    }

    public void ReleaseTeamReservation(string token)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM account_admission_reservations WHERE token=$token";
        command.Parameters.AddWithValue("$token", token);
        command.ExecuteNonQuery();
    }

    static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        string backend, string account, string reason, DateTimeOffset observed, DateTimeOffset? reset,
        string? job = null, string? session = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$backend", backend);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$observed", Stamp(observed));
        command.Parameters.AddWithValue("$reset", reset is null ? DBNull.Value : Stamp(reset.Value));
        if (job is not null) { command.Parameters.AddWithValue("$job", job); }
        if (job is not null) { command.Parameters.AddWithValue("$session", session ?? (object)DBNull.Value); }
        return command;
    }

    static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
}
