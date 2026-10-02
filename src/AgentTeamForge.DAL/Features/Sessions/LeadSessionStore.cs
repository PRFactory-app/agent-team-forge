using AgentTeamForge.DAL.Files;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.DAL.Features.Sessions;

public sealed record RecoverableLeadSession(string SessionId, int JobCount, string LastActivity);
public sealed record LeadSessionInfo(string SessionId, string Workspace, string LeadToken, int JobCount,
    IReadOnlyList<RecoverableLeadSession> RecoverableSessions)
{
    public string? Name { get; init; }
    public string Identity { get; init; } = "team-lead";
    public string Cwd => Workspace;
    public string SessionDir { get; init; } = string.Empty;
}

/// <summary>One durable lead per MCP binding. Session IDs are explicit recovery handles.</summary>
public sealed class LeadSessionStore(JobDatabase database)
{
    public LeadSessionInfo Start(string workspace, string bindingKey, string? nativeKind = null, string? nativeSessionId = null, string? nativeHome = null)
    {
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction(deferred: false);
        var existing = Sessions(connection, workspace).FirstOrDefault(s => s.BindingKey == bindingKey);
        var id = existing?.Id ?? Guid.NewGuid().ToString("D");
        var token = existing?.Token ?? Guid.NewGuid().ToString("N");
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO lead_sessions(session_id,workspace,binding_key,lead_token,updated_at,native_kind,native_session_id,native_home)
            VALUES ($id,$workspace,$binding,$token,$now,$kind,$native,$home)
            ON CONFLICT(session_id) DO UPDATE SET binding_key=$binding, updated_at=$now, native_kind=$kind, native_session_id=$native, native_home=$home
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$workspace", workspace);
        command.Parameters.AddWithValue("$binding", bindingKey);
        command.Parameters.AddWithValue("$kind", (object?)nativeKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$native", (object?)nativeSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$home", (object?)nativeHome ?? DBNull.Value);
        command.Parameters.AddWithValue("$token", token);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        tx.Commit();
        var sessionDir = Path.Combine(Path.GetDirectoryName(database.Path)!, "lead-sessions", id);
        PrivateFiles.CreateDirectory(sessionDir);
        return Info(id, workspace)!;
    }

    public LeadSessionInfo? Resume(string id, string workspace, string bindingKey, string? nativeKind = null, string? nativeSessionId = null, string? nativeHome = null)
    {
        if (!Guid.TryParseExact(id, "D", out var parsed) || parsed.ToString("D") != id)
        {
            return null;
        }
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        // Like the reference's binding prune: this binding now names only the resumed
        // session, so a bridge restart under the same parent re-adopts it, not the
        // empty session it started with.
        command.CommandText = """
            UPDATE lead_sessions SET binding_key=$binding, updated_at=$now, native_kind=$kind, native_session_id=$native, native_home=$home WHERE session_id=$id AND workspace=$workspace AND closed_at IS NULL;
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$binding", bindingKey);
        command.Parameters.AddWithValue("$kind", (object?)nativeKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$native", (object?)nativeSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$home", (object?)nativeHome ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$workspace", workspace);
        if ((long)command.ExecuteScalar()! != 1)
        {
            return null;
        }
        command.CommandText = "UPDATE lead_sessions SET binding_key='' WHERE binding_key=$binding AND workspace=$workspace AND session_id<>$id";
        command.ExecuteNonQuery();
        tx.Commit();
        return Info(id, workspace);
    }

    public LeadSessionInfo? Info(string id, string workspace)
    {
        using var connection = database.OpenConnection();
        var sessions = Sessions(connection, workspace);
        var current = sessions.FirstOrDefault(s => s.Id == id);
        if (current is null)
        {
            return null;
        }
        var recoverable = sessions.Where(s => s.Id != id && s.Count > 0)
            .Select(s => new RecoverableLeadSession(s.Id, s.Count, s.UpdatedAt)).ToList();
        return new LeadSessionInfo(id, workspace, current.Token, current.Count, recoverable)
        {
            Name = current.Name,
            SessionDir = Path.Combine(Path.GetDirectoryName(database.Path)!, "lead-sessions", id)
        };
    }

    public bool Rename(string id, string workspace, string? name)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE lead_sessions SET display_name=$n WHERE session_id=$id AND workspace=$ws AND closed_at IS NULL";
        command.Parameters.AddWithValue("$n", (object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$ws", workspace);
        return command.ExecuteNonQuery() == 1;
    }

    public bool Exists(string id, string workspace) => Info(id, workspace) is not null;

    public bool IsManagedChild(string id, string workspace, string jobId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM lead_sessions WHERE session_id=$id AND workspace=$workspace AND binding_key=$binding AND closed_at IS NULL";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$workspace", workspace);
        command.Parameters.AddWithValue("$binding", "managed-child:" + jobId);
        return (long)command.ExecuteScalar()! == 1;
    }

    /// <summary>Explicitly close a lead. Joined tokens are revoked in the same transaction.</summary>
    public bool Close(string id, string workspace)
    {
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE lead_sessions SET closed_at=$now,binding_key='',wake_key=NULL
            WHERE session_id=$id AND workspace=$workspace AND closed_at IS NULL
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$workspace", workspace);
        if (command.ExecuteNonQuery() != 1)
        {
            return false;
        }

        command.CommandText = """
            UPDATE external_teams SET closed_at=$now,wake_key=NULL WHERE lead_session_id=$id AND closed_at IS NULL;
            UPDATE external_members SET active=0,left_at=$now,wake_key=NULL
            WHERE team_id=$id AND left_at IS NULL;
            UPDATE external_messages SET wake_key=NULL,read_at=COALESCE(read_at,$now) WHERE team_id=$id;
            """;
        command.ExecuteNonQuery();
        tx.Commit();
        return true;
    }

    /// <summary>
    /// Close leads that can no longer be resumed or used: no jobs (so not in recoverable_sessions),
    /// idle for <paramref name="minAge"/>, and a recorded parent pid that is dead. Sessions with jobs
    /// stay open because closing makes them unresumable. A reused pid only keeps a row alive.
    /// </summary>
    public int CloseAbandoned(Func<int, bool> parentAlive, TimeSpan minAge)
    {
        var cutoff = DateTimeOffset.UtcNow - minAge;
        var candidates = new List<(string Id, string Workspace, int Pid)>();
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT s.session_id,s.workspace,s.binding_key,s.updated_at FROM lead_sessions s
                WHERE s.closed_at IS NULL AND s.binding_key LIKE 'identity=%'
                AND NOT EXISTS (SELECT 1 FROM jobs j WHERE j.lead_session_id=s.session_id)
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!DateTimeOffset.TryParse(reader.GetString(3), out var updated) || updated > cutoff) { continue; }
                var parent = reader.GetString(2).Split('\n').FirstOrDefault(l => l.StartsWith("parent=", StringComparison.Ordinal));
                if (parent is not null && int.TryParse(parent.AsSpan(7), out var pid) && pid > 0)
                {
                    candidates.Add((reader.GetString(0), reader.GetString(1), pid));
                }
            }
        }
        var closed = 0;
        foreach (var (id, workspace, pid) in candidates.Where(c => !parentAlive(c.Pid)))
        {
            if (Close(id, workspace)) { closed++; }
        }
        return closed;
    }

    /// <summary>Move unread notices to the current bridge after a restart or late wake registration.</summary>
    public void BindWake(string id, string key, long generation)
    {
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE lead_sessions SET wake_key=$key WHERE session_id=$id AND closed_at IS NULL
            AND EXISTS (SELECT 1 FROM wake_targets WHERE target_key=$key AND generation=$generation AND active=1);
            UPDATE external_teams SET wake_key=$key WHERE lead_session_id=$id AND closed_at IS NULL
            AND EXISTS (SELECT 1 FROM wake_targets WHERE target_key=$key AND generation=$generation AND active=1);
            UPDATE wake_jobs SET target_key=$key, read_at=NULL WHERE job_id IN
                (SELECT job_id FROM jobs WHERE lead_session_id=$id)
                AND read_at IS NULL AND EXISTS (SELECT 1 FROM wake_targets WHERE target_key=$key AND generation=$generation AND active=1);
            INSERT INTO wake_jobs(job_id,target_key)
                SELECT j.job_id,$key FROM jobs j WHERE j.lead_session_id=$id
                AND j.status IN ('queued','running')
                AND NOT EXISTS (SELECT 1 FROM wake_jobs w WHERE w.job_id=j.job_id)
                AND EXISTS (SELECT 1 FROM wake_targets WHERE target_key=$key AND generation=$generation AND active=1);
            UPDATE external_messages SET wake_key=$key WHERE team_id=$id AND recipient='lead'
                AND read_at IS NULL AND EXISTS (SELECT 1 FROM wake_targets WHERE target_key=$key AND generation=$generation AND active=1);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$generation", generation);
        command.ExecuteNonQuery();
        tx.Commit();
    }

    public IReadOnlyList<NativeSessionBinding> NativeBindings(IEnumerable<string> ids)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        var names = new List<string>();
        foreach (var id in ids.Distinct().Take(50))
        {
            var name = "$id" + names.Count;
            names.Add(name);
            command.Parameters.AddWithValue(name, id);
        }
        if (names.Count == 0) { return []; }
        command.CommandText = $"""
            SELECT s.session_id,
                CASE WHEN s.native_kind IS NOT NULL AND s.native_session_id IS NOT NULL THEN s.native_kind ELSE w.kind END,
                CASE WHEN s.native_kind IS NOT NULL AND s.native_session_id IS NOT NULL THEN s.native_session_id ELSE w.address END,
                CASE WHEN s.native_kind IS NOT NULL AND s.native_session_id IS NOT NULL THEN s.native_home ELSE w.home END
            FROM lead_sessions s LEFT JOIN wake_targets w ON w.target_key=s.wake_key AND w.kind='codex'
            WHERE s.closed_at IS NULL AND s.binding_key NOT LIKE 'managed-child:%' AND s.session_id IN ({string.Join(",", names)})
            """;
        using var reader = command.ExecuteReader();
        var result = new List<NativeSessionBinding>();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1) && !reader.IsDBNull(2))
            {
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }
        return result;
    }
    static List<SessionRow> Sessions(Microsoft.Data.Sqlite.SqliteConnection connection, string workspace)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.session_id,s.binding_key,s.lead_token,s.updated_at,count(j.job_id),s.display_name
            FROM lead_sessions s LEFT JOIN jobs j ON j.lead_session_id=s.session_id
            WHERE s.workspace=$workspace AND s.closed_at IS NULL GROUP BY s.session_id ORDER BY s.updated_at DESC
            """;
        command.Parameters.AddWithValue("$workspace", workspace);
        using var reader = command.ExecuteReader();
        var result = new List<SessionRow>();
        while (reader.Read())
        {
            result.Add(new SessionRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return result;
    }

    sealed record SessionRow(string Id, string BindingKey, string Token, string UpdatedAt, int Count, string? Name);
}

public sealed record NativeSessionBinding(string SessionId, string Kind, string NativeId, string? Home);
