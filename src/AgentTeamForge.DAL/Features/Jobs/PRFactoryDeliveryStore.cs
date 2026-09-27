namespace AgentTeamForge.DAL.Features.Jobs;

public sealed record PRFactoryManagedMember(string Member, int Turn, string JobId);
public sealed record PRFactoryPendingCommand(Guid Id, string Payload, string? ParentJob);
public sealed record PRFactoryStreamPosition(long Offset, long Seq, string? Status, string? Pending, int ResultOffset = 0);

public sealed partial class PRFactoryTeamStore
{
    public IReadOnlyList<PRFactoryManagedMember> ManagedMembers(string server, Guid id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT member,turn,job_id FROM prfactory_members WHERE server=$server AND work_item_id=$id ORDER BY member,turn";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        var rows = new List<PRFactoryManagedMember>();
        while (reader.Read()) { rows.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2))); }
        return rows;
    }

    /// <summary>A killed managed member stays closed: later messages must not resume its session.</summary>
    public void MarkManagedKilled(string server, Guid id, string member)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO prfactory_killed_members VALUES ($server,$id,$member)";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$member", member);
        command.ExecuteNonQuery();
    }

    public bool IsManagedKilled(string server, Guid id, string member)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM prfactory_killed_members WHERE server=$server AND work_item_id=$id AND member=$member";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$member", member);
        return command.ExecuteScalar() is not null;
    }

    public void SavePendingCommand(string server, Guid id, Guid commandId, string payload, string? parentJob)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO prfactory_pending_commands VALUES ($server,$id,$command,$payload,$parent)";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$command", commandId.ToString("D"));
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$parent", (object?)parentJob ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<PRFactoryPendingCommand> PendingCommands(string server, Guid id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT command_id,payload,parent_job FROM prfactory_pending_commands WHERE server=$server AND work_item_id=$id ORDER BY rowid";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        var rows = new List<PRFactoryPendingCommand>();
        while (reader.Read()) { rows.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2))); }
        return rows;
    }

    public void RemovePendingCommand(string server, Guid id, Guid commandId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM prfactory_pending_commands WHERE server=$server AND work_item_id=$id AND command_id=$command";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$command", commandId.ToString("D"));
        command.ExecuteNonQuery();
    }

    public PRFactoryStreamPosition StreamPosition(string server, Guid id, string jobId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT offset,seq,status,pending,result_offset FROM prfactory_stream_positions WHERE server=$server AND work_item_id=$id AND job_id=$job";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$job", jobId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4)) : new(0, 0, null, null);
    }

    public void SaveStreamPosition(string server, Guid id, string jobId, PRFactoryStreamPosition position)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prfactory_stream_positions VALUES ($server,$id,$job,$offset,$seq,$status,$pending,$result)
            ON CONFLICT(server,work_item_id,job_id) DO UPDATE SET offset=$offset,seq=$seq,status=$status,pending=$pending,result_offset=$result
            """;
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$offset", position.Offset);
        command.Parameters.AddWithValue("$seq", position.Seq);
        command.Parameters.AddWithValue("$status", (object?)position.Status ?? DBNull.Value);
        command.Parameters.AddWithValue("$pending", (object?)position.Pending ?? DBNull.Value);
        command.Parameters.AddWithValue("$result", position.ResultOffset);
        command.ExecuteNonQuery();
    }
}
