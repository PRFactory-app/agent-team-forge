using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.DAL.Features.Jobs;

public sealed record PRFactoryTeamRecord(string Server, Guid WorkItemId, string ClaimedJson, string State, bool Uploaded);

/// <summary>Local work ownership and job mappings; the server acceptance protocol is a later slice.</summary>
public sealed class PRFactoryTeamStore(JobDatabase database)
{
    public bool CreateIfAbsent(string server, Guid id, string claimedJson)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO prfactory_teams(server, work_item_id, claimed_json, created_at, updated_at)
            VALUES ($server, $id, $json, $now, $now)
            """;
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$json", claimedJson);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return command.ExecuteNonQuery() == 1;
    }

    public PRFactoryTeamRecord? Get(string server, Guid id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT claimed_json, state, uploaded FROM prfactory_teams WHERE server=$server AND work_item_id=$id";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new PRFactoryTeamRecord(server, id, reader.GetString(0), reader.GetString(1), reader.GetInt32(2) != 0) : null;
    }

    public IReadOnlyList<PRFactoryTeamRecord> Pending(string server)
    {
        var rows = new List<PRFactoryTeamRecord>();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT work_item_id, claimed_json, state, uploaded FROM prfactory_teams WHERE server=$server AND state='claimed' ORDER BY created_at";
        command.Parameters.AddWithValue("$server", server);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new(server, Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0));
        }

        return rows;
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
