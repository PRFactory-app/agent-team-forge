using System.Text.Json;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.DAL.Features.Jobs;

public sealed record BaseRefreshRecord(string WorkspaceKey, string OriginalSha, string CurrentBaseSha,
    string Action, string State);
public sealed record WipRecord(string WorkspaceKey, string Branch, string HeadSha, string? RemoteOldSha,
    string State, string? Receipt);
public sealed record WipReleaseRecord(string ReleaseId, string VerifiedWipSha, DateTimeOffset AcknowledgedAt);

/// <summary>Durable Git intents. A pending refresh is rolled back before a resumed phase runs.</summary>
public sealed class PRFactoryHandoverStore(JobDatabase database)
{
    public BaseRefreshRecord? Refresh(string key)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT original_sha,current_base_sha,action,state FROM prfactory_base_refresh WHERE workspace_key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        using var row = cmd.ExecuteReader();
        return row.Read() ? new(key, row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3)) : null;
    }

    public void BeginRefresh(BaseRefreshRecord record)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO prfactory_base_refresh VALUES ($key,$old,$base,$action,'pending') ON CONFLICT(workspace_key) DO UPDATE SET original_sha=$old,current_base_sha=$base,action=$action,state='pending'";
        cmd.Parameters.AddWithValue("$key", record.WorkspaceKey);
        cmd.Parameters.AddWithValue("$old", record.OriginalSha);
        cmd.Parameters.AddWithValue("$base", record.CurrentBaseSha);
        cmd.Parameters.AddWithValue("$action", record.Action);
        cmd.ExecuteNonQuery();
    }

    public void FinishRefresh(string key, string state)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE prfactory_base_refresh SET state=$state WHERE workspace_key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Moves the recorded base/start to the refreshed tip and marks the refresh done in one transaction.</summary>
    public void CompleteRefresh(string key, string baseSha, string startingSha)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction();
        using var read = db.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT snapshot FROM prfactory_workspaces WHERE workspace_key=$key";
        read.Parameters.AddWithValue("$key", key);
        var snapshot = read.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize(json, WorkspaceJson.Default.WorkspaceSnapshot)
            : throw new InvalidOperationException("Workspace missing.");
        using var write = db.CreateCommand();
        write.Transaction = tx;
        write.CommandText = """
            UPDATE prfactory_workspaces SET snapshot=$snapshot WHERE workspace_key=$key;
            UPDATE prfactory_base_refresh SET state='done' WHERE workspace_key=$key AND state='pending';
            """;
        write.Parameters.AddWithValue("$key", key);
        write.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(snapshot! with { BaseSha = baseSha, StartingSha = startingSha },
            WorkspaceJson.Default.WorkspaceSnapshot));
        write.ExecuteNonQuery();
        tx.Commit();
    }

    public WipRecord? Wip(string key)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT branch,head_sha,remote_old_sha,state,receipt FROM prfactory_wip WHERE workspace_key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        using var row = cmd.ExecuteReader();
        return row.Read() ? new(key, row.GetString(0), row.GetString(1), row.IsDBNull(2) ? null : row.GetString(2),
            row.GetString(3), row.IsDBNull(4) ? null : row.GetString(4)) : null;
    }

    public void SaveWip(WipRecord record)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO prfactory_wip VALUES ($key,$branch,$head,$old,$state,$receipt) ON CONFLICT(workspace_key) DO UPDATE SET branch=$branch,head_sha=$head,remote_old_sha=$old,state=$state,receipt=$receipt";
        cmd.Parameters.AddWithValue("$key", record.WorkspaceKey);
        cmd.Parameters.AddWithValue("$branch", record.Branch);
        cmd.Parameters.AddWithValue("$head", record.HeadSha);
        cmd.Parameters.AddWithValue("$old", (object?)record.RemoteOldSha ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$state", record.State);
        cmd.Parameters.AddWithValue("$receipt", (object?)record.Receipt ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public WipReleaseRecord? Release(string key)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT release_id,verified_wip_sha,acknowledged_at FROM prfactory_wip_releases WHERE workspace_key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        using var row = cmd.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetString(1), DateTimeOffset.Parse(row.GetString(2))) : null;
    }

    public void RecordRelease(string key, WipReleaseRecord receipt)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO prfactory_wip_releases VALUES ($key,$id,$sha,$at)";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$id", receipt.ReleaseId);
        cmd.Parameters.AddWithValue("$sha", receipt.VerifiedWipSha);
        cmd.Parameters.AddWithValue("$at", receipt.AcknowledgedAt.ToString("O"));
        cmd.ExecuteNonQuery();
        if (Release(key) is not { } saved || saved.ReleaseId != receipt.ReleaseId || saved.VerifiedWipSha != receipt.VerifiedWipSha)
        {
            throw new InvalidOperationException("Release acknowledgement changed; reconciliation required.");
        }
    }
}
