using System.Security.Cryptography;
using System.Text;
using AgentTeamForge.DAL.Sqlite;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.DAL.Features.External;

public sealed record JoinTicket(string SessionId, string Name, string Token, DateTimeOffset ExpiresAt)
{
    public string JoinPrompt => $"Join my AgentTeamForge team as {Name}. Call join_team(session_id=\"{SessionId}\", token=\"{Token}\"). Save member_token from the reply. Call external_read(member_token=...) to read messages, external_send(member_token=..., text=...) to reply, and leave_team(member_token=...) when finished.";
}
public sealed record JoinedMember(string SessionId, string Name, string MemberToken);
public sealed record ExternalMessage(long Seq, string From, string Text, string CreatedAt);
public sealed record ExternalInbox(IReadOnlyList<ExternalMessage> Messages, long NextSeq, bool HasMore);

/// <summary>Ticket, membership and inbox transactions. A token only selects its own active membership.</summary>
public sealed class ExternalMemberStore(JobDatabase database)
{
    static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
    static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public JoinTicket? CreateTicket(string sessionId, string workspace, string name, string note, DateTimeOffset now, TimeSpan ttl)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction(deferred: false);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT count(*) FROM lead_sessions WHERE session_id=$session AND workspace=$workspace AND closed_at IS NULL";
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$workspace", workspace);
        if ((long)command.ExecuteScalar()! != 1)
        {
            return null;
        }

        command.CommandText = "SELECT name FROM external_members WHERE session_id=$session";
        var names = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }
        }
        var reserved = name;
        for (var n = 2; names.Contains(reserved); n++)
        {
            var suffix = "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            reserved = name[..Math.Min(name.Length, 64 - suffix.Length)] + suffix;
        }
        var ticket = Secret();
        var expires = now + ttl;
        command.CommandText = """
            INSERT INTO external_members(member_id,session_id,name,note,ticket_hash,ticket_expires,created_at)
            VALUES ($id,$session,$name,$note,$hash,$expires,$now)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$name", reserved);
        command.Parameters.AddWithValue("$note", note);
        command.Parameters.AddWithValue("$hash", Hash(ticket));
        command.Parameters.AddWithValue("$expires", expires.ToString("O"));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        try { command.ExecuteNonQuery(); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return null; }
        tx.Commit();
        return new JoinTicket(sessionId, reserved, ticket, expires);
    }

    public JoinedMember? Join(string sessionId, string ticket, DateTimeOffset now)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction(deferred: false);
        using var select = db.CreateCommand();
        select.Transaction = tx;
        select.CommandText = """
            SELECT m.name FROM external_members m JOIN lead_sessions s ON s.session_id=m.session_id
            WHERE m.session_id=$session AND m.ticket_hash=$hash
            AND m.ticket_used_at IS NULL AND m.ticket_expires>$now AND m.left_at IS NULL AND s.closed_at IS NULL
            """;
        select.Parameters.AddWithValue("$session", sessionId);
        select.Parameters.AddWithValue("$hash", Hash(ticket));
        select.Parameters.AddWithValue("$now", now.ToString("O"));
        if (select.ExecuteScalar() is not string name)
        {
            return null;
        }

        var token = Secret();
        using var update = db.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            UPDATE external_members SET ticket_used_at=$now,token_hash=$token,active=1
            WHERE session_id=$session AND ticket_hash=$hash AND ticket_used_at IS NULL
            """;
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$session", sessionId);
        update.Parameters.AddWithValue("$hash", Hash(ticket));
        update.Parameters.AddWithValue("$token", Hash(token));
        if (update.ExecuteNonQuery() != 1)
        {
            return null;
        }

        tx.Commit();
        return new JoinedMember(sessionId, name, token);
    }

    public bool Leave(string token, DateTimeOffset now)
    {
        using var db = database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = """
            UPDATE external_members SET active=0,left_at=$now,token_hash=NULL,wake_key=NULL
            WHERE token_hash=$hash AND active=1
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$hash", Hash(token));
        return command.ExecuteNonQuery() == 1;
    }

    public void RevokeSession(string sessionId, DateTimeOffset now)
    {
        using var db = database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = """
            UPDATE external_members SET active=0,left_at=$now,token_hash=NULL,wake_key=NULL
            WHERE session_id=$session AND left_at IS NULL
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$session", sessionId);
        command.ExecuteNonQuery();
    }

    public bool SendFromMember(string token, string text, DateTimeOffset now)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction(deferred: false);
        var member = FindMember(db, tx, token);
        if (member is null)
        {
            return false;
        }

        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO external_messages(session_id,sender,recipient,text,created_at,wake_key)
            SELECT $session,$sender,'lead',$text,$now,wake_key FROM lead_sessions WHERE session_id=$session
            """;
        command.Parameters.AddWithValue("$session", member.Value.Session);
        command.Parameters.AddWithValue("$sender", member.Value.Name);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        if (command.ExecuteNonQuery() != 1)
        {
            return false;
        }

        tx.Commit();
        return true;
    }

    public bool SendFromLead(string sessionId, string workspace, string name, string text, DateTimeOffset now)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction(deferred: false);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO external_messages(session_id,sender,recipient,text,created_at,wake_key)
            SELECT m.session_id,'team-lead',m.member_id,$text,$now,m.wake_key
            FROM external_members m JOIN lead_sessions s ON s.session_id=m.session_id
            WHERE m.session_id=$session AND s.workspace=$workspace AND m.name=$name AND m.active=1
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$workspace", workspace);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        if (command.ExecuteNonQuery() != 1)
        {
            return false;
        }

        tx.Commit();
        return true;
    }

    public ExternalInbox? ReadMember(string token, long sinceSeq, int limit, DateTimeOffset now)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction(deferred: false);
        var member = FindMember(db, tx, token);
        if (member is null)
        {
            return null;
        }

        var inbox = Read(db, tx, member.Value.Session, member.Value.Id, sinceSeq, limit, now);
        tx.Commit();
        return inbox;
    }

    public ExternalInbox? ReadLead(string sessionId, string workspace, long sinceSeq, int limit, DateTimeOffset now)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction(deferred: false);
        using var check = db.CreateCommand();
        check.Transaction = tx;
        check.CommandText = "SELECT count(*) FROM lead_sessions WHERE session_id=$session AND workspace=$workspace AND closed_at IS NULL";
        check.Parameters.AddWithValue("$session", sessionId);
        check.Parameters.AddWithValue("$workspace", workspace);
        if ((long)check.ExecuteScalar()! != 1)
        {
            return null;
        }

        var inbox = Read(db, tx, sessionId, "lead", sinceSeq, limit, now);
        tx.Commit();
        return inbox;
    }

    public bool SetMemberWake(string token, string? wakeKey)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction(deferred: false);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE external_members SET wake_key=$key WHERE token_hash=$hash AND active=1";
        command.Parameters.AddWithValue("$key", (object?)wakeKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", Hash(token));
        if (command.ExecuteNonQuery() != 1)
        {
            return false;
        }

        command.CommandText = """
            UPDATE external_messages SET wake_key=$key WHERE read_at IS NULL
            AND recipient=(SELECT member_id FROM external_members WHERE token_hash=$hash AND active=1)
            """;
        command.ExecuteNonQuery();
        tx.Commit();
        return true;
    }

    public bool IsActive(string token)
    {
        using var db = database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM external_members WHERE token_hash=$hash AND active=1";
        command.Parameters.AddWithValue("$hash", Hash(token));
        return (long)command.ExecuteScalar()! == 1;
    }

    static (string Id, string Session, string Name)? FindMember(SqliteConnection db, SqliteTransaction tx, string token)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT member_id,session_id,name FROM external_members WHERE token_hash=$hash AND active=1 AND left_at IS NULL";
        command.Parameters.AddWithValue("$hash", Hash(token));
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    static ExternalInbox Read(SqliteConnection db, SqliteTransaction tx, string session, string recipient, long sinceSeq, int limit, DateTimeOffset now)
    {
        using var select = db.CreateCommand();
        select.Transaction = tx;
        select.CommandText = """
            SELECT seq,sender,text,created_at FROM external_messages
            WHERE session_id=$session AND recipient=$recipient AND seq>$since ORDER BY seq LIMIT $limit
            """;
        select.Parameters.AddWithValue("$session", session);
        select.Parameters.AddWithValue("$recipient", recipient);
        select.Parameters.AddWithValue("$since", sinceSeq);
        select.Parameters.AddWithValue("$limit", limit + 1);
        var rows = new List<ExternalMessage>();
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(new ExternalMessage(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        var more = rows.Count > limit;
        if (more)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var next = rows.Count == 0 ? sinceSeq : rows[^1].Seq;
        if (rows.Count > 0)
        {
            using var update = db.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE external_messages SET read_at=$now WHERE session_id=$session AND recipient=$recipient
                AND seq>$since AND seq<=$next AND read_at IS NULL
                """;
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$session", session);
            update.Parameters.AddWithValue("$recipient", recipient);
            update.Parameters.AddWithValue("$since", sinceSeq);
            update.Parameters.AddWithValue("$next", next);
            update.ExecuteNonQuery();
        }
        return new ExternalInbox(rows, next, more);
    }

    public int Prune(DateTimeOffset cutoff, bool dryRun)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction(deferred: false);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = dryRun
            ? "SELECT count(*) FROM external_messages WHERE created_at<$cutoff"
            : "DELETE FROM external_messages WHERE created_at<$cutoff";
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        var count = dryRun ? Convert.ToInt32(command.ExecuteScalar()) : command.ExecuteNonQuery();
        if (!dryRun)
        {
            command.CommandText = "DELETE FROM external_members WHERE (left_at IS NOT NULL OR ticket_used_at IS NULL AND ticket_expires<$cutoff) AND created_at<$cutoff";
            command.ExecuteNonQuery();
            tx.Commit();
        }
        return count;
    }
}
