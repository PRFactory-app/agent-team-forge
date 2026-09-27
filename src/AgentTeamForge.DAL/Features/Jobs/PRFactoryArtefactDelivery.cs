namespace AgentTeamForge.DAL.Features.Jobs;

public sealed record PRFactoryArtefactDelivery(string? Payload, string? Failure);

public sealed partial class PRFactoryTeamStore
{
    public PRFactoryArtefactDelivery? ArtefactDelivery(string server, Guid id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload, failure FROM prfactory_artefact_delivery WHERE server=$server AND work_item_id=$id";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)) : null;
    }

    public void FreezeArtefacts(string server, Guid id, string? payload, string? failure)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prfactory_artefact_delivery(server, work_item_id, payload, failure)
            VALUES ($server, $id, $payload, $failure)
            ON CONFLICT(server, work_item_id) DO UPDATE SET failure=coalesce(failure, excluded.failure)
            """;
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
        command.Parameters.AddWithValue("$failure", (object?)failure ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
