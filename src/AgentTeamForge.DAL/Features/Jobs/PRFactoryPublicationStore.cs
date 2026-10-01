using System.Text.Json;
using System.Text.Json.Serialization;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.DAL.Features.Jobs;

// PublicationId is stable for one accepted phase/attempt and repository, including after restart.
public sealed record PublicationIntent(string PublicationId, string Server, Guid WorkItemId, Guid LeaseToken,
    string MachineId, string JobId, string RepositoryId, string WorkspaceKey, string LeadPath,
    string Remote, string InternalBranch, string PublishBranch, string BaseSha, string HeadSha, string? ExpectedRemoteSha);
public sealed record PublicationReceipt(PublicationIntent Intent, string? VerifiedAt);

[JsonSerializable(typeof(PublicationIntent))]
internal partial class PublicationJson : JsonSerializerContext;

public sealed class PRFactoryPublicationStore(JobDatabase database)
{
    public PublicationReceipt? Get(string id)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT intent,verified_at FROM prfactory_publications WHERE publication_id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new(JsonSerializer.Deserialize(reader.GetString(0), PublicationJson.Default.PublicationIntent)!,
            reader.IsDBNull(1) ? null : reader.GetString(1)) : null;
    }

    /// <summary>Verified publications this machine made for one accepted work item and repository.</summary>
    public IReadOnlyList<PublicationIntent> VerifiedFor(string server, Guid workItemId, Guid repositoryId)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT intent FROM prfactory_publications WHERE verified_at IS NOT NULL";
        using var reader = cmd.ExecuteReader();
        var found = new List<PublicationIntent>();
        while (reader.Read())
        {
            var intent = JsonSerializer.Deserialize(reader.GetString(0), PublicationJson.Default.PublicationIntent)!;
            if (intent.Server == server && intent.WorkItemId == workItemId
                && Guid.TryParse(intent.RepositoryId, out var repository) && repository == repositoryId)
            {
                found.Add(intent);
            }
        }
        return found;
    }

    public PublicationReceipt SaveIntent(PublicationIntent intent)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO prfactory_publications (publication_id,intent) VALUES ($id,$intent)";
        cmd.Parameters.AddWithValue("$id", intent.PublicationId);
        cmd.Parameters.AddWithValue("$intent", JsonSerializer.Serialize(intent, PublicationJson.Default.PublicationIntent));
        cmd.ExecuteNonQuery();
        var saved = Get(intent.PublicationId)!;
        if (saved.Intent != intent) { throw new InvalidOperationException("Publication identity or frozen output changed; reconciliation required."); }
        return saved;
    }

    public PublicationReceipt Verify(PublicationIntent intent)
    {
        SaveIntent(intent);
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE prfactory_publications SET verified_at=COALESCE(verified_at,$at) WHERE publication_id=$id";
        cmd.Parameters.AddWithValue("$id", intent.PublicationId);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return Get(intent.PublicationId)!;
    }
}
