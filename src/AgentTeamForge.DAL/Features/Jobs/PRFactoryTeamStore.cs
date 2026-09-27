using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.DAL.Features.Jobs;

public sealed record PRFactoryTeamRecord(string Server, Guid WorkItemId, string ClaimedJson, string State, bool Uploaded,
    Guid? MachineId, string? AtfJobId, string AcceptanceState);
public sealed record PRFactoryExternalRecord(string Member, string ActualName, string TeamId, string TicketToken,
    DateTimeOffset TicketExpires, bool TicketUploaded, long ReplySeq, bool Closed);
public sealed record PRFactoryCommandReceipt(bool Accepted, string? Reason);

/// <summary>Local work ownership, durable acceptance identity, and job mappings.</summary>
public sealed partial class PRFactoryTeamStore(JobDatabase database)
{
    public bool CreateIfAbsent(string server, Guid id, string claimedJson, Guid? machineId = null)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO prfactory_teams(server, work_item_id, claimed_json, created_at, updated_at,
                machine_id, atf_job_id, acceptance_state)
            VALUES ($server, $id, $json, $now, $now, $machine, $job, $acceptance)
            """;
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$json", claimedJson);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$machine", machineId is Guid machine ? machine.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$job", machineId is null ? DBNull.Value : "atf:" + Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$acceptance", machineId is null ? "legacy" : "pending");
        return command.ExecuteNonQuery() == 1;
    }

    public PRFactoryTeamRecord? Get(string server, Guid id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT claimed_json, state, uploaded, machine_id, atf_job_id, acceptance_state FROM prfactory_teams WHERE server=$server AND work_item_id=$id";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTeam(server, id, reader, 0) : null;
    }

    public IReadOnlyList<PRFactoryTeamRecord> Pending(string server)
    {
        var rows = new List<PRFactoryTeamRecord>();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT work_item_id, claimed_json, state, uploaded, machine_id, atf_job_id, acceptance_state FROM prfactory_teams WHERE server=$server AND state='claimed' ORDER BY created_at";
        command.Parameters.AddWithValue("$server", server);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadTeam(server, Guid.Parse(reader.GetString(0)), reader, 1));
        }

        return rows;
    }

    static PRFactoryTeamRecord ReadTeam(string server, Guid id, Microsoft.Data.Sqlite.SqliteDataReader reader, int offset) =>
        new(server, id, reader.GetString(offset), reader.GetString(offset + 1), reader.GetInt32(offset + 2) != 0,
            reader.IsDBNull(offset + 3) ? null : Guid.Parse(reader.GetString(offset + 3)),
            reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4), reader.GetString(offset + 5));

    public IReadOnlyList<PRFactoryTeamRecord> ReconciliationNeeded(string server)
    {
        var rows = new List<PRFactoryTeamRecord>();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT work_item_id, claimed_json, state, uploaded, machine_id, atf_job_id, acceptance_state FROM prfactory_teams WHERE server=$server AND acceptance_state='reconciliation_needed' ORDER BY created_at";
        command.Parameters.AddWithValue("$server", server);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadTeam(server, Guid.Parse(reader.GetString(0)), reader, 1));
        }
        return rows;
    }

    public void SetAcceptance(string server, Guid id, string acceptanceState)
    {
        if (acceptanceState is not ("accepted" or "legacy" or "reconciliation_needed"))
        {
            throw new ArgumentOutOfRangeException(nameof(acceptanceState));
        }
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE prfactory_teams SET acceptance_state=$acceptance, updated_at=$now WHERE server=$server AND work_item_id=$id";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$acceptance", acceptanceState);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public string? MemberJob(string server, Guid id, string member, int turn)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT job_id FROM prfactory_members WHERE server=$server AND work_item_id=$id AND member=$member AND turn=$turn";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$member", member);
        command.Parameters.AddWithValue("$turn", turn);
        return command.ExecuteScalar() as string;
    }

    public IReadOnlyList<string> MemberJobs(string server, Guid id)
    {
        var jobs = new List<string>();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT job_id FROM prfactory_members WHERE server=$server AND work_item_id=$id";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            jobs.Add(reader.GetString(0));
        }

        return jobs;
    }

    public void RecordMember(string server, Guid id, string member, int turn, string jobId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prfactory_members(server, work_item_id, member, turn, job_id)
            VALUES ($server, $id, $member, $turn, $job)
            ON CONFLICT(server, work_item_id, member, turn) DO NOTHING
            """;
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$member", member);
        command.Parameters.AddWithValue("$turn", turn);
        command.Parameters.AddWithValue("$job", jobId);
        command.ExecuteNonQuery();
    }

    public PRFactoryExternalRecord? External(string server, Guid id, string member) =>
        ExternalMembers(server, id).FirstOrDefault(row => row.Member == member);

    public IReadOnlyList<PRFactoryExternalRecord> ExternalMembers(string server, Guid id)
    {
        var rows = new List<PRFactoryExternalRecord>();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT member,actual_name,team_id,ticket_token,ticket_expires,ticket_uploaded,reply_seq,closed FROM prfactory_external WHERE server=$server AND work_item_id=$id ORDER BY member";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)), reader.GetInt32(5) != 0, reader.GetInt64(6), reader.GetInt32(7) != 0));
        }
        return rows;
    }

    public void RecordExternal(string server, Guid id, string member, string actualName, string teamId, string token, DateTimeOffset expires)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO prfactory_external(server,work_item_id,member,actual_name,team_id,ticket_token,ticket_expires)
            VALUES ($server,$id,$member,$actual,$team,$token,$expires)
            """;
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$member", member);
        command.Parameters.AddWithValue("$actual", actualName);
        command.Parameters.AddWithValue("$team", teamId);
        command.Parameters.AddWithValue("$token", token);
        command.Parameters.AddWithValue("$expires", expires.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void RenewExternal(string server, Guid id, string member, string token, DateTimeOffset expires)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE prfactory_external SET ticket_token=$token,ticket_expires=$expires WHERE server=$server AND work_item_id=$id AND member=$member";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$member", member);
        command.Parameters.AddWithValue("$token", token);
        command.Parameters.AddWithValue("$expires", expires.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void MarkTicketUploaded(string server, Guid id, string member) => UpdateExternal(server, id, member, "ticket_uploaded=1");
    public void SetReplySeq(string server, Guid id, string member, long seq) => UpdateExternal(server, id, member, "reply_seq=$seq", seq);
    public void MarkExternalClosed(string server, Guid id) => UpdateExternal(server, id, null, "closed=1");
    public void MarkExternalClosed(string server, Guid id, string member) => UpdateExternal(server, id, member, "closed=1");

    void UpdateExternal(string server, Guid id, string? member, string set, long? seq = null)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE prfactory_external SET {set} WHERE server=$server AND work_item_id=$id" + (member is null ? "" : " AND member=$member");
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        if (member is not null)
        {
            command.Parameters.AddWithValue("$member", member);
        }

        if (seq is not null)
        {
            command.Parameters.AddWithValue("$seq", seq.Value);
        }

        command.ExecuteNonQuery();
    }

    public PRFactoryCommandReceipt? CommandReceipt(string server, Guid id, Guid commandId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT accepted,reason FROM prfactory_command_receipts WHERE server=$server AND work_item_id=$id AND command_id=$command";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$command", commandId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt32(0) != 0, reader.IsDBNull(1) ? null : reader.GetString(1)) : null;
    }

    public void RecordCommand(string server, Guid id, Guid commandId, bool accepted, string? reason)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO prfactory_command_receipts(server,work_item_id,command_id,accepted,reason) VALUES ($server,$id,$command,$accepted,$reason)";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$command", commandId.ToString("D"));
        command.Parameters.AddWithValue("$accepted", accepted ? 1 : 0);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void SetUploaded(string server, Guid id)
    {
        Update(server, id, "uploaded=1");
    }

    public void Finish(string server, Guid id, string state)
    {
        if (state is not ("completed" or "failed" or "refused"))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        Update(server, id, "state=$state", state);
    }

    void Update(string server, Guid id, string set, string? state = null)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE prfactory_teams SET {set}, updated_at=$now WHERE server=$server AND work_item_id=$id";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        if (state is not null)
        {
            command.Parameters.AddWithValue("$state", state);
        }

        command.ExecuteNonQuery();
    }
}
