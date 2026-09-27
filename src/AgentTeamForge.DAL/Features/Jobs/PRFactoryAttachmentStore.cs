namespace AgentTeamForge.DAL.Features.Jobs;

public sealed record PRFactoryAttachment(string ClientKey, string ContentType, byte[] Payload);

public sealed partial class PRFactoryTeamStore
{
    // Null means not collected; an empty list means the immutable batch has no pending uploads.
    public List<PRFactoryAttachment>? PendingAttachments(string server, Guid id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM prfactory_attachment_batches WHERE server=$server AND work_item_id=$id";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        if ((long)command.ExecuteScalar()! == 0) { return null; }
        command.CommandText = "SELECT client_key, content_type, payload FROM prfactory_attachments WHERE server=$server AND work_item_id=$id AND uploaded=0 ORDER BY client_key";
        using var reader = command.ExecuteReader();
        var rows = new List<PRFactoryAttachment>();
        while (reader.Read()) { rows.Add(new(reader.GetString(0), reader.GetString(1), (byte[])reader[2])); }
        return rows;
    }

    public void FreezeAttachments(string server, Guid id, IReadOnlyList<PRFactoryAttachment> uploads)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO prfactory_attachment_batches(server, work_item_id) VALUES ($server, $id)";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        if (command.ExecuteNonQuery() == 0) { return; }
        foreach (var upload in uploads)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO prfactory_attachments(server,work_item_id,client_key,content_type,payload) VALUES ($server,$id,$key,$type,$payload)";
            insert.Parameters.AddWithValue("$server", server);
            insert.Parameters.AddWithValue("$id", id.ToString("D"));
            insert.Parameters.AddWithValue("$key", upload.ClientKey);
            insert.Parameters.AddWithValue("$type", upload.ContentType);
            insert.Parameters.AddWithValue("$payload", upload.Payload);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void AttachmentUploaded(string server, Guid id, string key)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE prfactory_attachments SET uploaded=1 WHERE server=$server AND work_item_id=$id AND client_key=$key";
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }
}
