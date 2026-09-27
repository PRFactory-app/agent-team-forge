using System.Text.Json;
using System.Text.Json.Serialization;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.DAL.Features.Jobs;

public sealed record WorkspaceMember(string Name, int Order, string Path, string? Branch);
public sealed record WorkspaceSnapshot(string Key, string? RepositoryId, string? Remote, string? RepositoryPath,
    string? BaseBranch, string? BaseSha, string? StartingSha, string? InternalBranch, string? PublishBranch,
    string Root, string LeadPath, string StagingPath, bool ReadOnly, WorkspaceMember[] Members);
public sealed record WorkspaceIntegration(int Order, string ChildHead, string BeforeSha, string AfterSha, bool Applied);

[JsonSerializable(typeof(WorkspaceSnapshot))]
internal partial class WorkspaceJson : JsonSerializerContext;

public sealed class PRFactoryWorkspaceStore(JobDatabase database)
{
    public WorkspaceSnapshot? Get(string key)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT snapshot FROM prfactory_workspaces WHERE workspace_key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize(json, WorkspaceJson.Default.WorkspaceSnapshot) : null;
    }

    public void Save(WorkspaceSnapshot snapshot)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO prfactory_workspaces VALUES ($key,$snapshot)";
        cmd.Parameters.AddWithValue("$key", snapshot.Key);
        cmd.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(snapshot, WorkspaceJson.Default.WorkspaceSnapshot));
        cmd.ExecuteNonQuery();
        if (JsonSerializer.Serialize(Get(snapshot.Key), WorkspaceJson.Default.WorkspaceSnapshot) !=
            JsonSerializer.Serialize(snapshot, WorkspaceJson.Default.WorkspaceSnapshot))
        {
            throw new InvalidOperationException("Workspace identity is immutable.");
        }
    }

    public WorkspaceIntegration? Integration(string key, int order)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT child_head,before_sha,after_sha,applied FROM prfactory_workspace_integrations WHERE workspace_key=$key AND member_order=$order";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$order", order);
        using var row = cmd.ExecuteReader();
        return row.Read() ? new(order, row.GetString(0), row.GetString(1), row.GetString(2), row.GetBoolean(3)) : null;
    }

    public void SaveIntent(string key, WorkspaceIntegration intent)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO prfactory_workspace_integrations VALUES ($key,$order,$child,$before,$after,0)";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$order", intent.Order);
        cmd.Parameters.AddWithValue("$child", intent.ChildHead);
        cmd.Parameters.AddWithValue("$before", intent.BeforeSha);
        cmd.Parameters.AddWithValue("$after", intent.AfterSha);
        cmd.ExecuteNonQuery();
    }

    public void MarkApplied(string key, int order)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE prfactory_workspace_integrations SET applied=1 WHERE workspace_key=$key AND member_order=$order";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$order", order);
        cmd.ExecuteNonQuery();
    }
}
