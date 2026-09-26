using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.DAL.Features.Sessions;

public sealed record RecoverableLeadSession(string SessionId, int JobCount, string LastActivity);
public sealed record LeadSessionInfo(string SessionId, string Workspace, string LeadToken, int JobCount,
    IReadOnlyList<RecoverableLeadSession> RecoverableSessions)
{
    public string Identity { get; init; } = "team-lead";
    public string Cwd => Workspace;
    public string SessionDir { get; init; } = string.Empty;
}

/// <summary>One durable lead per MCP binding. Session IDs are explicit recovery handles.</summary>
public sealed class LeadSessionStore(JobDatabase database)
{
    public LeadSessionInfo Start(string workspace, string bindingKey)
    {
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction(deferred: false);
        var existing = Sessions(connection, workspace).FirstOrDefault(s => s.BindingKey == bindingKey);
        var id = existing?.Id ?? Guid.NewGuid().ToString("D");
        var token = existing?.Token ?? Guid.NewGuid().ToString("N");
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO lead_sessions(session_id,workspace,binding_key,lead_token,updated_at)
            VALUES ($id,$workspace,$binding,$token,$now)
            ON CONFLICT(session_id) DO UPDATE SET binding_key=$binding, updated_at=$now
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$workspace", workspace);
        command.Parameters.AddWithValue("$binding", bindingKey);
        command.Parameters.AddWithValue("$token", token);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        tx.Commit();
        var sessionDir = Path.Combine(Path.GetDirectoryName(database.Path)!, "lead-sessions", id);
        Directory.CreateDirectory(sessionDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return Info(id, workspace)!;
    }

    public LeadSessionInfo? Resume(string id, string workspace, string bindingKey)
    {
        if (!Guid.TryParseExact(id, "D", out var parsed) || parsed.ToString("D") != id)
        {
            return null;
        }
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE lead_sessions SET binding_key=$binding, updated_at=$now WHERE session_id=$id AND workspace=$workspace";
        command.Parameters.AddWithValue("$binding", bindingKey);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$workspace", workspace);
        return command.ExecuteNonQuery() == 1 ? Info(id, workspace) : null;
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
            SessionDir = Path.Combine(Path.GetDirectoryName(database.Path)!, "lead-sessions", id)
        };
    }

    public bool Exists(string id, string workspace) => Info(id, workspace) is not null;

    /// <summary>Move unread notices to the current bridge after a restart or late wake registration.</summary>
    public void BindWake(string id, string key, long generation)
    {
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE lead_sessions SET wake_key=$key WHERE session_id=$id
            AND EXISTS (SELECT 1 FROM wake_targets WHERE target_key=$key AND generation=$generation);
            UPDATE wake_jobs SET target_key=$key, read_at=NULL WHERE job_id IN
                (SELECT job_id FROM jobs WHERE lead_session_id=$id)
                AND read_at IS NULL AND EXISTS (SELECT 1 FROM wake_targets WHERE target_key=$key AND generation=$generation);
            INSERT INTO wake_jobs(job_id,target_key)
                SELECT j.job_id,$key FROM jobs j WHERE j.lead_session_id=$id
                AND j.status IN ('completed','failed','needs_reconciliation','cancelled')
                AND NOT EXISTS (SELECT 1 FROM wake_jobs w WHERE w.job_id=j.job_id)
                AND EXISTS (SELECT 1 FROM wake_targets WHERE target_key=$key AND generation=$generation);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$generation", generation);
        command.ExecuteNonQuery();
        tx.Commit();
    }

    static List<SessionRow> Sessions(Microsoft.Data.Sqlite.SqliteConnection connection, string workspace)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.session_id,s.binding_key,s.lead_token,s.updated_at,count(j.job_id)
            FROM lead_sessions s LEFT JOIN jobs j ON j.lead_session_id=s.session_id
            WHERE s.workspace=$workspace GROUP BY s.session_id ORDER BY s.updated_at DESC
            """;
        command.Parameters.AddWithValue("$workspace", workspace);
        using var reader = command.ExecuteReader();
        var result = new List<SessionRow>();
        while (reader.Read())
        {
            result.Add(new SessionRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));
        }
        return result;
    }

    sealed record SessionRow(string Id, string BindingKey, string Token, string UpdatedAt, int Count);
}
