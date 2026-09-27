using System.Text.Json;
using System.Text.Json.Serialization;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.DAL.Features.Jobs;

public sealed record RepositorySetMember(string Id, string Name, string Remote, string BaseBranch,
    string MappingPath, string WorkspaceKey, bool ReadOnly, string? BaseCommitSha = null,
    string? StartFromBranch = null, string? StartCommitSha = null, string? PublishBranch = null,
    string? PlanBasisCommitSha = null);
public sealed record RepositorySetSnapshot(string WorkspaceKey, RepositorySetMember[] Members, string ManifestPath);
public sealed record RepositoryRefreshOriginal(string WorkspaceKey, string BaseSha, string StartingSha, string HeadSha,
    string? CurrentBaseSha = null);
public sealed record RepositoryRefreshBatch(string WorkspaceKey, RepositoryRefreshOriginal[] Originals, string State);

[JsonSerializable(typeof(RepositorySetSnapshot))]
[JsonSerializable(typeof(RepositoryRefreshOriginal[]))]
internal sealed partial class RepositorySetJson : JsonSerializerContext;

public sealed class PRFactoryRepositorySetStore(JobDatabase database)
{
    public RepositorySetSnapshot? Get(string key)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT snapshot FROM prfactory_repository_sets WHERE workspace_key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize(json, RepositorySetJson.Default.RepositorySetSnapshot) : null;
    }

    public void Save(RepositorySetSnapshot snapshot)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO prfactory_repository_sets VALUES ($key,$snapshot)";
        cmd.Parameters.AddWithValue("$key", snapshot.WorkspaceKey);
        cmd.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(snapshot, RepositorySetJson.Default.RepositorySetSnapshot));
        cmd.ExecuteNonQuery();
        if (JsonSerializer.Serialize(Get(snapshot.WorkspaceKey), RepositorySetJson.Default.RepositorySetSnapshot) !=
            JsonSerializer.Serialize(snapshot, RepositorySetJson.Default.RepositorySetSnapshot))
        {
            throw new InvalidOperationException("Accepted repository set changed; reconciliation required.");
        }
    }

    public RepositoryRefreshBatch? Refresh(string key)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT originals,state FROM prfactory_multi_refresh WHERE workspace_key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        using var row = cmd.ExecuteReader();
        return row.Read() ? new(key, JsonSerializer.Deserialize(row.GetString(0), RepositorySetJson.Default.RepositoryRefreshOriginalArray)!, row.GetString(1)) : null;
    }

    public void BeginRefresh(string key, RepositoryRefreshOriginal[] originals)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO prfactory_multi_refresh VALUES ($key,$originals,'pending') ON CONFLICT(workspace_key) DO UPDATE SET originals=$originals,state='pending'";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$originals", JsonSerializer.Serialize(originals, RepositorySetJson.Default.RepositoryRefreshOriginalArray));
        cmd.ExecuteNonQuery();
    }

    public void FinishRefresh(string key, string state)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE prfactory_multi_refresh SET state=$state WHERE workspace_key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.ExecuteNonQuery();
    }
}
