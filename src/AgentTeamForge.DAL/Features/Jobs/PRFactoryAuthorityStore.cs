using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.DAL.Features.Jobs;

public sealed record PRFactoryAuthorityRecord(Guid WorkItemId, string Disposition, string? Reason, bool Stopping);

/// <summary>Separate from acceptance identity: fencing never releases or erases accepted ownership.</summary>
public sealed class PRFactoryAuthorityStore(JobDatabase database)
{
    public IReadOnlyList<PRFactoryAuthorityRecord> Read(string server)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT work_item_id, disposition, reason, stopping FROM prfactory_authority WHERE server=$server";
        command.Parameters.AddWithValue("$server", server);
        using var reader = command.ExecuteReader();
        var rows = new List<PRFactoryAuthorityRecord>();
        while (reader.Read())
        {
            rows.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3)));
        }
        return rows;
    }

    public void Set(string server, Guid id, string disposition, string? reason)
    {
        if (disposition is not ("accepted" or "completed" or "cancelled" or "revoked" or "reconciliation-needed"))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        // An ordinary acceptance observation cannot resurrect fenced work. Reconciliation is explicit.
        command.CommandText = """
            INSERT INTO prfactory_authority(server,work_item_id,disposition,reason,stopping)
            VALUES($server,$id,$disposition,$reason,$stopping)
            ON CONFLICT(server,work_item_id) DO UPDATE SET
                disposition=excluded.disposition,reason=excluded.reason,stopping=excluded.stopping
            WHERE prfactory_authority.disposition='accepted'
               OR (prfactory_authority.disposition='reconciliation-needed' AND excluded.disposition IN ('cancelled','revoked','completed'))
            """;
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$disposition", disposition);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$stopping", disposition == "accepted" ? 0 : 1);
        command.ExecuteNonQuery();
    }

    public void Stopped(string server, Guid id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE prfactory_authority SET stopping=0 WHERE server=$server AND work_item_id=$id AND disposition<>'accepted'";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<string> OwnedTurns(string server, Guid id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE owned(job_id) AS (
                SELECT job_id FROM prfactory_members WHERE server=$server AND work_item_id=$id
                UNION
                SELECT j.job_id FROM jobs j JOIN owned o ON j.parent_job_id=o.job_id)
            SELECT job_id FROM owned
            """;
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) { rows.Add(reader.GetString(0)); }
        return rows;
    }
}
