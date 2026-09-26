using System.Security.Cryptography;
using System.Text;
using AgentTeamForge.DAL.Sqlite;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.DAL.Features.External;

public sealed record JoinTicket(string SessionId, string Name, string Token, DateTimeOffset ExpiresAt)
{
    public string JoinPrompt => $"Join my AgentTeamForge team as {Name} using the external-member MCP entry. Call join_team(session_id=\"{SessionId}\", token=\"{Token}\"). Save member_token from the reply. In Codex Desktop, read CODEX_THREAD_ID and the absolute CODEX_HOME for this conversation (default $HOME/.codex), then call external_set_wake(member_token=..., codex_thread_id=..., codex_home=...) to receive queue notices. Call external_read(member_token=...) to read work, external_send(member_token=..., text=...) to reply, and leave_team(member_token=...) only when finished permanently.";
}
public sealed record JoinedMember(string SessionId, string Name, string MemberToken);
public sealed record ExternalMessage(long Seq, string From, string Text, string CreatedAt, bool? Truncated = null, int? FullLen = null)
{
    public string Ts => CreatedAt;
}
public sealed record ExternalInbox(IReadOnlyList<ExternalMessage> Messages, long NextSeq, bool HasMore,
    IReadOnlyDictionary<string, long>? Cursors = null, long? SenderSeq = null, int? UnreadCount = null);

/// <summary>Ticket, membership and inbox transactions. A token only selects its own active membership.</summary>
public sealed class ExternalMemberStore(JobDatabase database)
{
    static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
    static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>Idempotently attach an MCP lead session to a team of the same ID.</summary>
    public bool EnsureMcpTeam(string sessionId, string workspace, DateTimeOffset now)
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
            return false;
        }

        command.CommandText = """
            INSERT OR IGNORE INTO external_teams(team_id,owner_key,lead_session_id,wake_key,created_at)
            SELECT session_id,'mcp:'||session_id,session_id,wake_key,$now FROM lead_sessions
            WHERE session_id=$session AND workspace=$workspace AND closed_at IS NULL
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.ExecuteNonQuery();
        command.CommandText = "SELECT count(*) FROM external_teams WHERE team_id=$session AND lead_session_id=$session AND closed_at IS NULL";
        var attached = (long)command.ExecuteScalar()! == 1;
        tx.Commit();
        return attached;
    }

    /// <summary>Create or recover a durable team for an in-daemon actor's stable owner key.</summary>
    public string? CreateActorTeam(string ownerKey, DateTimeOffset now)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction(deferred: false);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT OR IGNORE INTO external_teams(team_id,owner_key,created_at) VALUES ($id,$owner,$now);
            SELECT team_id FROM external_teams WHERE owner_key=$owner AND closed_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$owner", ownerKey);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        var id = command.ExecuteScalar() as string;
        tx.Commit();
        return id;
    }

    public JoinTicket? CreateTicket(string teamId, string name, string note, DateTimeOffset now, TimeSpan ttl)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction(deferred: false);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT count(*) FROM external_teams WHERE team_id=$team AND closed_at IS NULL";
        command.Parameters.AddWithValue("$team", teamId);
        if ((long)command.ExecuteScalar()! != 1)
        {
            return null;
        }

        command.CommandText = "SELECT name FROM external_members WHERE team_id=$team";
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
            INSERT INTO external_members(member_id,team_id,name,note,ticket_hash,ticket_expires,created_at)
            VALUES ($id,$team,$name,$note,$hash,$expires,$now)
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
        return new JoinTicket(teamId, reserved, ticket, expires);
    }

    /// <summary>Replace an expired, never-used ticket for the same reserved member name.</summary>
    public JoinTicket? RenewExpiredTicket(string teamId, string name, DateTimeOffset now, TimeSpan ttl)
    {
        using var db = database.OpenConnection();
        using var command = db.CreateCommand();
        var ticket = Secret();
        var expires = now + ttl;
        command.CommandText = """
            UPDATE external_members SET ticket_hash=$hash,ticket_expires=$expires
            WHERE team_id=$team AND name=$name AND ticket_used_at IS NULL AND left_at IS NULL AND ticket_expires<=$now
            AND EXISTS (SELECT 1 FROM external_teams t WHERE t.team_id=$team AND t.closed_at IS NULL)
            """;
        command.Parameters.AddWithValue("$team", teamId);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$hash", Hash(ticket));
        command.Parameters.AddWithValue("$expires", expires.ToString("O"));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        return command.ExecuteNonQuery() == 1 ? new JoinTicket(teamId, name, ticket, expires) : null;
    }

    public bool HasLeft(string teamId, string name)
    {
        using var db = database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM external_members WHERE team_id=$team AND name=$name AND left_at IS NOT NULL";
        command.Parameters.AddWithValue("$team", teamId);
        command.Parameters.AddWithValue("$name", name);
        return (long)command.ExecuteScalar()! > 0;
    }

    public (JoinedMember? Member, bool Left) Join(string teamId, string ticket, DateTimeOffset now)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction(deferred: false);
        using var select = db.CreateCommand();
        select.Transaction = tx;
        select.CommandText = """
            SELECT m.name,m.ticket_used_at,m.token_hash,m.member_id,m.left_at,m.ticket_expires FROM external_members m JOIN external_teams t ON t.team_id=m.team_id
            WHERE m.team_id=$team AND m.ticket_hash=$hash
            AND t.closed_at IS NULL
            """;
        select.Parameters.AddWithValue("$team", teamId);
        select.Parameters.AddWithValue("$hash", Hash(ticket));
        select.Parameters.AddWithValue("$now", now.ToString("O"));
        string name;
        bool used;
        string? storedHash;
        string memberId;
        using (var reader = select.ExecuteReader())
        {
            if (!reader.Read())
            {
                return (null, false);
            }

            name = reader.GetString(0);
            used = !reader.IsDBNull(1);
            storedHash = reader.IsDBNull(2) ? null : reader.GetString(2);
            memberId = reader.GetString(3);
            if (!reader.IsDBNull(4))
            {
                return (null, true);
            }
            if (string.CompareOrdinal(reader.GetString(5), now.ToString("O")) <= 0)
            {
                return (null, false);
            }
        }

        // The ticket remains the recovery credential until expiry. Derivation makes a lost
        // join reply recoverable without storing a bearer token in plaintext; the private
        // member ID keeps a leaked ticket from deriving the token offline after expiry.
        var token = Hash("atf-member:" + memberId + ":" + ticket);
        if (used)
        {
            return (storedHash == Hash(token) ? new JoinedMember(teamId, name, token) : null, false);
        }
        using var update = db.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            UPDATE external_members SET ticket_used_at=$now,token_hash=$token,active=1
            WHERE team_id=$team AND ticket_hash=$hash AND ticket_used_at IS NULL
            """;
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$team", teamId);
        update.Parameters.AddWithValue("$hash", Hash(ticket));
        update.Parameters.AddWithValue("$token", Hash(token));
        if (update.ExecuteNonQuery() != 1)
        {
            return (null, false);
        }

        tx.Commit();
        return (new JoinedMember(teamId, name, token), false);
    }

    // 0 = unknown/revoked, 1 = left now, 2 = already left.
    public (int State, string? Name) Leave(string token, DateTimeOffset now)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction(deferred: false);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        // Revoked members get no more doorbells for mail they never read.
        command.CommandText = """
            UPDATE external_messages SET wake_key=NULL,read_at=$now WHERE read_at IS NULL
            AND recipient=(SELECT member_id FROM external_members WHERE token_hash=$hash AND active=1);
            UPDATE external_members SET active=0,left_at=$now,wake_key=NULL
            WHERE token_hash=$hash AND active=1;
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$hash", Hash(token));
        if ((long)command.ExecuteScalar()! != 1)
        {
            command.CommandText = "SELECT name FROM external_members WHERE token_hash=$hash AND active=0 AND left_at IS NOT NULL";
            var previousName = command.ExecuteScalar() as string;
            return (previousName is null ? 0 : 2, previousName);
        }

        command.CommandText = "SELECT name FROM external_members WHERE token_hash=$hash";
        var name = command.ExecuteScalar() as string;
        tx.Commit();
        return (1, name);
    }

    public bool CloseTeam(string teamId, DateTimeOffset now)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction(deferred: false);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE external_teams SET closed_at=$now,wake_key=NULL WHERE team_id=$team AND closed_at IS NULL";
        command.Parameters.AddWithValue("$team", teamId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        if (command.ExecuteNonQuery() != 1)
        {
            return false;
        }
        command.CommandText = """
            UPDATE external_members SET active=0,left_at=$now,wake_key=NULL
            WHERE team_id=$team AND left_at IS NULL;
            UPDATE external_messages SET wake_key=NULL,read_at=COALESCE(read_at,$now) WHERE team_id=$team;
            """;
        command.ExecuteNonQuery();
        tx.Commit();
        return true;
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
            INSERT INTO external_messages(team_id,sender,recipient,sender_seq,text,created_at,wake_key)
            SELECT $team,$sender,'lead',$position,$text,$now,wake_key FROM external_teams WHERE team_id=$team AND closed_at IS NULL
            """;
        command.Parameters.AddWithValue("$team", member.Value.Team);
        command.Parameters.AddWithValue("$sender", member.Value.Name);
        command.Parameters.AddWithValue("$position", NextSenderSeq(db, tx, member.Value.Team, "lead", member.Value.Name));
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        if (command.ExecuteNonQuery() != 1)
        {
            return false;
        }

        tx.Commit();
        return true;
    }

    public bool SendToMember(string teamId, string name, string text, string sender, DateTimeOffset now, string? commandId = null)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction(deferred: false);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$team", teamId);
        if (commandId is not null)
        {
            command.CommandText = "INSERT OR IGNORE INTO external_delivery_keys(team_id,command_id) VALUES ($team,$command)";
            command.Parameters.AddWithValue("$command", commandId);
            if (command.ExecuteNonQuery() == 0)
            {
                tx.Commit();
                return true;
            }
        }
        command.CommandText = """
            INSERT INTO external_messages(team_id,sender,recipient,sender_seq,text,created_at,wake_key)
            SELECT m.team_id,$sender,m.member_id,$position,$text,$now,m.wake_key
            FROM external_members m JOIN external_teams t ON t.team_id=m.team_id
            WHERE m.team_id=$team AND t.closed_at IS NULL AND m.name=$name AND m.active=1
            """;
        command.Parameters.AddWithValue("$sender", sender);
        command.Parameters.AddWithValue("$name", name);
        // A failed send rolls back this allocation with the transaction.
        using (var recipient = db.CreateCommand())
        {
            recipient.Transaction = tx;
            recipient.CommandText = "SELECT member_id FROM external_members WHERE team_id=$team AND name=$name AND active=1";
            recipient.Parameters.AddWithValue("$team", teamId);
            recipient.Parameters.AddWithValue("$name", name);
            if (recipient.ExecuteScalar() is not string recipientId)
            {
                return false;
            }
            command.Parameters.AddWithValue("$position", NextSenderSeq(db, tx, teamId, recipientId, sender));
        }
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        if (command.ExecuteNonQuery() != 1)
        {
            return false;
        }

        tx.Commit();
        return true;
    }

    static long NextSenderSeq(SqliteConnection db, SqliteTransaction tx, string teamId, string recipient, string sender)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT OR IGNORE INTO external_sender_cursors(team_id,recipient,sender) VALUES ($team,$recipient,$sender);
            UPDATE external_sender_cursors SET high_water=high_water+1
            WHERE team_id=$team AND recipient=$recipient AND sender=$sender;
            SELECT high_water FROM external_sender_cursors
            WHERE team_id=$team AND recipient=$recipient AND sender=$sender;
            """;
        command.Parameters.AddWithValue("$team", teamId);
        command.Parameters.AddWithValue("$recipient", recipient);
        command.Parameters.AddWithValue("$sender", sender);
        return (long)command.ExecuteScalar()!;
    }

    public ExternalInbox? ReadMemberCompat(string token, long? sinceSeq, int limit, DateTimeOffset now, string? fromAgent)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction(deferred: false);
        var member = FindMember(db, tx, token);
        if (member is null)
        {
            return null;
        }

        var inbox = ReadCompat(db, tx, member.Value.Team, member.Value.Id, sinceSeq, limit, now, fromAgent);
        tx.Commit();
        return inbox;
    }

    public ExternalInbox? ReadLeadCompat(string teamId, long? sinceSeq, int limit, DateTimeOffset now, string? fromAgent)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction(deferred: false);
        using var check = db.CreateCommand();
        check.Transaction = tx;
        check.CommandText = "SELECT count(*) FROM external_teams WHERE team_id=$team AND closed_at IS NULL";
        check.Parameters.AddWithValue("$team", teamId);
        if ((long)check.ExecuteScalar()! != 1)
        {
            return null;
        }

        var inbox = ReadCompat(db, tx, teamId, "lead", sinceSeq, limit, now, fromAgent);
        tx.Commit();
        return inbox;
    }

    static ExternalInbox ReadCompat(SqliteConnection db, SqliteTransaction tx, string teamId, string recipient,
        long? sinceSeq, int limit, DateTimeOffset now, string? fromAgent)
    {
        using var select = db.CreateCommand();
        select.Transaction = tx;
        select.CommandText = "SELECT sender_seq,sender,text,created_at FROM external_messages WHERE team_id=$team AND recipient=$recipient ORDER BY seq";
        select.Parameters.AddWithValue("$team", teamId);
        select.Parameters.AddWithValue("$recipient", recipient);
        var positions = new Dictionary<string, long>(StringComparer.Ordinal);
        var cursors = new Dictionary<string, long>(StringComparer.Ordinal);
        var available = new List<ExternalMessage>();
        using (var watermarks = db.CreateCommand())
        {
            watermarks.Transaction = tx;
            watermarks.CommandText = "SELECT sender,high_water,cursor FROM external_sender_cursors WHERE team_id=$team AND recipient=$recipient";
            watermarks.Parameters.AddWithValue("$team", teamId);
            watermarks.Parameters.AddWithValue("$recipient", recipient);
            using var reader = watermarks.ExecuteReader();
            while (reader.Read())
            {
                positions[reader.GetString(0)] = reader.GetInt64(1);
                cursors[reader.GetString(0)] = reader.GetInt64(2);
            }
        }
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                available.Add(new ExternalMessage(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }
        var floor = fromAgent is null ? 0 : Math.Max(cursors.GetValueOrDefault(fromAgent), sinceSeq ?? 0);
        var pending = available.Where(message => (fromAgent is null || message.From == fromAgent)
            && message.Seq > (fromAgent is null ? cursors.GetValueOrDefault(message.From) : floor)).ToList();
        var unread = pending.Count;
        var selected = pending.Take(limit).ToList();
        if (limit != 0)
        {
            var updated = new Dictionary<string, long>(cursors, StringComparer.Ordinal);
            if (fromAgent is not null)
            {
                updated[fromAgent] = Math.Min(positions.GetValueOrDefault(fromAgent), floor);
            }

            foreach (var message in selected)
            {
                updated[message.From] = message.Seq;
            }

            using var update = db.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE external_messages SET read_at=$now
                WHERE team_id=$team AND recipient=$recipient AND sender=$sender
                AND sender_seq<=$cursor AND read_at IS NULL
                """;
            update.Parameters.AddWithValue("$team", teamId);
            update.Parameters.AddWithValue("$recipient", recipient);
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.Add("$sender", SqliteType.Text);
            update.Parameters.Add("$cursor", SqliteType.Integer);
            foreach (var (sender, cursor) in updated)
            {
                if (cursor <= cursors.GetValueOrDefault(sender))
                {
                    continue;
                }

                update.Parameters["$sender"].Value = sender;
                update.Parameters["$cursor"].Value = cursor;
                update.ExecuteNonQuery();
                using var advance = db.CreateCommand();
                advance.Transaction = tx;
                advance.CommandText = """
                    UPDATE external_sender_cursors SET cursor=$cursor
                    WHERE team_id=$team AND recipient=$recipient AND sender=$sender AND cursor<$cursor
                    """;
                advance.Parameters.AddWithValue("$team", teamId);
                advance.Parameters.AddWithValue("$recipient", recipient);
                advance.Parameters.AddWithValue("$sender", sender);
                advance.Parameters.AddWithValue("$cursor", cursor);
                advance.ExecuteNonQuery();
            }
            cursors = updated;
        }
        return new ExternalInbox(selected, selected.Count == 0 ? 0 : selected[^1].Seq,
            unread > selected.Count, fromAgent is null ? cursors : null,
            fromAgent is null ? null : cursors.GetValueOrDefault(fromAgent), unread);
    }

    public ExternalInbox? ReadTeam(string teamId, long? sinceSeq, int limit, DateTimeOffset now)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction(deferred: false);
        using var check = db.CreateCommand();
        check.Transaction = tx;
        check.CommandText = "SELECT count(*) FROM external_teams WHERE team_id=$team AND closed_at IS NULL";
        check.Parameters.AddWithValue("$team", teamId);
        if ((long)check.ExecuteScalar()! != 1)
        {
            return null;
        }

        var inbox = Read(db, tx, teamId, "lead", sinceSeq, limit, now);
        tx.Commit();
        return inbox;
    }

    public bool BindTeamWake(string teamId, string wakeKey, long generation)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction(deferred: false);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE external_teams SET wake_key=$key WHERE team_id=$team AND closed_at IS NULL
            AND EXISTS (SELECT 1 FROM wake_targets WHERE target_key=$key AND generation=$generation)
            """;
        command.Parameters.AddWithValue("$team", teamId);
        command.Parameters.AddWithValue("$key", wakeKey);
        command.Parameters.AddWithValue("$generation", generation);
        if (command.ExecuteNonQuery() != 1)
        {
            return false;
        }
        command.CommandText = "UPDATE external_messages SET wake_key=$key WHERE team_id=$team AND recipient='lead' AND read_at IS NULL";
        command.ExecuteNonQuery();
        tx.Commit();
        return true;
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

    public bool TokenBelongsToTeam(string token, string teamId)
    {
        using var db = database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM external_members WHERE token_hash=$hash AND team_id=$team";
        command.Parameters.AddWithValue("$hash", Hash(token));
        command.Parameters.AddWithValue("$team", teamId);
        return (long)command.ExecuteScalar()! == 1;
    }

    static (string Id, string Team, string Name)? FindMember(SqliteConnection db, SqliteTransaction tx, string token)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT m.member_id,m.team_id,m.name FROM external_members m JOIN external_teams t ON t.team_id=m.team_id
            WHERE m.token_hash=$hash AND m.active=1 AND m.left_at IS NULL AND t.closed_at IS NULL
            """;
        command.Parameters.AddWithValue("$hash", Hash(token));
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    static ExternalInbox Read(SqliteConnection db, SqliteTransaction tx, string teamId, string recipient, long? sinceSeq, int limit, DateTimeOffset now, string? fromAgent = null)
    {
        using var select = db.CreateCommand();
        select.Transaction = tx;
        select.CommandText = """
            SELECT seq,sender,text,created_at FROM external_messages
            WHERE team_id=$team AND recipient=$recipient
            AND ($since IS NULL AND read_at IS NULL OR seq>$since)
            AND ($sender IS NULL OR sender=$sender) ORDER BY seq LIMIT $limit
            """;
        select.Parameters.AddWithValue("$team", teamId);
        select.Parameters.AddWithValue("$recipient", recipient);
        // No cursor drains unread rows; an explicit cursor re-reads from that point.
        select.Parameters.AddWithValue("$since", (object?)sinceSeq ?? DBNull.Value);
        select.Parameters.AddWithValue("$limit", limit + 1);
        select.Parameters.AddWithValue("$sender", (object?)fromAgent ?? DBNull.Value);
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

        var next = rows.Count == 0 ? sinceSeq ?? 0 : rows[^1].Seq;
        if (rows.Count > 0)
        {
            using var update = db.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE external_messages SET read_at=$now WHERE team_id=$team AND recipient=$recipient
                AND seq>=$first AND seq<=$next AND read_at IS NULL
                AND ($sender IS NULL OR sender=$sender)
                """;
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$team", teamId);
            update.Parameters.AddWithValue("$recipient", recipient);
            update.Parameters.AddWithValue("$first", rows[0].Seq);
            update.Parameters.AddWithValue("$next", next);
            update.Parameters.AddWithValue("$sender", (object?)fromAgent ?? DBNull.Value);
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
            ? "SELECT count(*) FROM external_messages WHERE created_at<$cutoff AND read_at IS NOT NULL"
            : "DELETE FROM external_messages WHERE created_at<$cutoff AND read_at IS NOT NULL";
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        var count = dryRun ? Convert.ToInt32(command.ExecuteScalar()) : command.ExecuteNonQuery();
        if (!dryRun)
        {
            command.CommandText = """
                DELETE FROM external_members WHERE created_at<$cutoff AND
                (ticket_used_at IS NULL AND ticket_expires<$cutoff OR
                 left_at IS NOT NULL AND team_id IN
                    (SELECT team_id FROM external_teams WHERE closed_at IS NOT NULL))
                """;
            command.ExecuteNonQuery();
            tx.Commit();
        }
        return count;
    }
}
