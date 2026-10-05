using AgentTeamForge.DAL.Files;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.DAL.Features.Sessions;

public sealed record RecoverableLeadSession(string SessionId, int JobCount, string LastActivity,
    string? OwnerNativeId = null, bool OwnerLive = false, bool IsCurrent = false);
/// <summary>The native session a lead row is bound to, with its active wake target if any. Pi bridges report no
/// native id, so a Pi lead is identified by its active pi:&lt;pid&gt; wake target.</summary>
public sealed record LeadSessionOwner(string NativeId, string? WakeKey, string? WakeKind, string? WakeAddress, string? WakeSecret, string? WakeHome)
{
    public bool Is(string? nativeId, string? wakeKey) => NativeId == nativeId || WakeKey is not null && WakeKey == wakeKey;
}
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
    /// <summary>Whether a bound native session is still running; set by the daemon. Unset means never live.</summary>
    public Func<LeadSessionOwner, bool>? OwnerLive { get; set; }

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

    /// <summary>Adopt a session. A session bound to another live native session is refused (LiveOwner set) unless forced.</summary>
    public (LeadSessionInfo? Session, string? LiveOwner) Resume(string id, string workspace, string bindingKey, string? nativeKind = null, string? nativeSessionId = null, string? nativeHome = null, bool force = false, string? callerWakeKey = null)
    {
        if (!Guid.TryParseExact(id, "D", out var parsed) || parsed.ToString("D") != id)
        {
            return (null, null);
        }
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction(deferred: false);
        if (!force && Sessions(connection, workspace).FirstOrDefault(s => s.Id == id) is { Owner: { } owner }
            && !owner.Is(nativeSessionId, callerWakeKey) && IsLive(owner))
        {
            return (null, owner.NativeId);
        }
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
            return (null, null);
        }
        command.CommandText = "UPDATE lead_sessions SET binding_key='' WHERE binding_key=$binding AND workspace=$workspace AND session_id<>$id";
        command.ExecuteNonQuery();
        tx.Commit();
        return (Info(id, workspace), null);
    }

    /// <summary>Address a retained job and atomically rebind its prior session. Native kind/home identify
    /// the local owner across native session changes; unknown identities and managed children never auto-adopt.</summary>
    public LeadJobReach ReachJob(string jobId, string callerId, string workspace, DateTimeOffset now)
    {
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT lead_session_id,accepted_at FROM jobs WHERE job_id=$job";
        command.Parameters.AddWithValue("$job", jobId);
        string? lead;
        DateTimeOffset accepted;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) { return new("unknown_job"); }
            lead = reader.IsDBNull(0) ? null : reader.GetString(0);
            accepted = DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture);
        }
        var expires = accepted.AddDays(30);
        if (now > expires) { return new("expired", PreviousSessionId: lead, ExpiresAt: expires); }
        if (lead is null || lead == callerId) { return new(); }
        var rows = Sessions(connection, workspace);
        var previous = rows.FirstOrDefault(s => s.Id == lead);
        var caller = rows.FirstOrDefault(s => s.Id == callerId);
        if (previous is null || caller is null) { return new("owned_by_previous_session", PreviousSessionId: lead); }
        if (previous.Owner is { } owner && !owner.Is(caller.Owner?.NativeId, caller.Owner?.WakeKey) && IsLive(owner))
        {
            return new("owned_by_live_lead", PreviousSessionId: lead, LiveOwner: previous.Name ?? owner.NativeId);
        }
        if (!SameOwner(previous, caller)) { return new("owned_by_previous_session", PreviousSessionId: lead); }
        // Reuse resume_session's binding-prune model: keep the durable team/session and its inbox,
        // rebind it to the caller instead of copying jobs or inventing a new inbox cursor.
        command.CommandText = """
            UPDATE lead_sessions SET binding_key=$binding,native_kind=$kind,native_session_id=$native,
                native_home=$home,updated_at=$now WHERE session_id=$lead;
            UPDATE lead_sessions SET binding_key='' WHERE workspace=$workspace AND binding_key=$binding AND session_id<>$lead;
            """;
        command.Parameters.AddWithValue("$binding", caller.BindingKey);
        command.Parameters.AddWithValue("$kind", (object?)caller.NativeKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$native", (object?)caller.Owner?.NativeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$home", (object?)caller.NativeHome ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$lead", lead);
        command.Parameters.AddWithValue("$workspace", workspace);
        command.ExecuteNonQuery();
        tx.Commit();
        return new(Session: Info(lead, workspace), PreviousSessionId: lead);
    }

    // read_messages has no required job id. Recover the most recently active eligible prior team
    // when this bridge has no jobs, or when a sender names a managed child via job_id.
    public string? RecoverableJob(string callerId, string workspace, DateTimeOffset now)
    {
        using var connection = database.OpenConnection();
        var rows = Sessions(connection, workspace);
        var caller = rows.FirstOrDefault(s => s.Id == callerId);
        if (caller is null || caller.Count > 0) { return null; }
        foreach (var row in rows.Where(s => s.Count > 0 && SameOwner(s, caller)
            && (s.Owner is null || s.Owner.Is(caller.Owner?.NativeId, caller.Owner?.WakeKey) || !IsLive(s.Owner))))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT job_id FROM jobs WHERE lead_session_id=$lead AND accepted_at >= $cutoff ORDER BY updated_at DESC LIMIT 1";
            command.Parameters.AddWithValue("$lead", row.Id);
            command.Parameters.AddWithValue("$cutoff", now.AddDays(-30).ToString("O"));
            if (command.ExecuteScalar() is string job) { return job; }
        }
        return null;
    }

    static bool SameOwner(SessionRow previous, SessionRow caller) =>
        !previous.BindingKey.StartsWith("managed-child:", StringComparison.Ordinal)
        && !caller.BindingKey.StartsWith("managed-child:", StringComparison.Ordinal)
        && previous.NativeKind is { Length: > 0 } && previous.NativeKind == caller.NativeKind
        && previous.NativeHome is { Length: > 0 } && string.Equals(previous.NativeHome, caller.NativeHome, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public LeadSessionInfo? Info(string id, string workspace)
    {
        using var connection = database.OpenConnection();
        var sessions = Sessions(connection, workspace);
        var current = sessions.FirstOrDefault(s => s.Id == id);
        if (current is null)
        {
            return null;
        }
        // Sessions bound to another live native session belong to that lead; never offer them for adoption.
        var recoverable = sessions.Where(s => s.Id != id && s.Count > 0)
            .Select(s => (Row: s, IsCurrent: s.Owner is { } owner && current.Owner is { } self && owner.Is(self.NativeId, self.WakeKey)))
            .Select(x => new RecoverableLeadSession(x.Row.Id, x.Row.Count, x.Row.UpdatedAt, x.Row.Owner?.NativeId,
                x.Row.Owner is { } owner && IsLive(owner), x.IsCurrent))
            .Where(s => s.IsCurrent || !s.OwnerLive).ToList();
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

    public bool Exists(string id, string workspace)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM lead_sessions WHERE session_id=$id AND workspace=$workspace AND closed_at IS NULL";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$workspace", workspace);
        return (long)command.ExecuteScalar()! == 1;
    }

    bool IsLive(LeadSessionOwner owner) => OwnerLive?.Invoke(owner) == true;

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
            SELECT s.session_id,s.binding_key,s.lead_token,s.updated_at,count(j.job_id),s.display_name,
                s.native_session_id,t.target_key,t.kind,t.address,t.secret,t.home,s.native_kind,s.native_home
            FROM lead_sessions s LEFT JOIN jobs j ON j.lead_session_id=s.session_id
            LEFT JOIN wake_targets t ON t.target_key=s.wake_key AND t.active=1
            WHERE s.workspace=$workspace AND s.closed_at IS NULL GROUP BY s.session_id ORDER BY s.updated_at DESC
            """;
        command.Parameters.AddWithValue("$workspace", workspace);
        using var reader = command.ExecuteReader();
        var result = new List<SessionRow>();
        while (reader.Read())
        {
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            var owner = Text(6) is { Length: > 0 } native ? new LeadSessionOwner(native, Text(7), Text(8), Text(9), Text(10), Text(11))
                : Text(8) == "pi" ? new LeadSessionOwner(Text(7)!, Text(7), "pi", Text(9), Text(10), Text(11)) : null;
            result.Add(new SessionRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), Text(5), owner, Text(12), Text(13)));
        }
        return result;
    }

    sealed record SessionRow(string Id, string BindingKey, string Token, string UpdatedAt, int Count, string? Name, LeadSessionOwner? Owner, string? NativeKind, string? NativeHome);
}

public sealed record NativeSessionBinding(string SessionId, string Kind, string NativeId, string? Home);

public sealed record LeadJobReach(string? Error = null, LeadSessionInfo? Session = null, string? PreviousSessionId = null, string? LiveOwner = null, DateTimeOffset? ExpiresAt = null);
