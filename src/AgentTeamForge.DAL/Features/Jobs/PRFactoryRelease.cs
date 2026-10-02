namespace AgentTeamForge.DAL.Features.Jobs;

public enum PRFactoryReleaseResult { Released, NotFound, NotFenced, StopNotAcknowledged, NeedsReconciliation, ExternalMemberOpen }

public sealed partial class PRFactoryTeamStore
{
    /// <summary>
    /// Forgets a fenced team so the next poll can claim the item afresh. One immediate transaction, so it is
    /// race-free with a running daemon: a later authority write for the released item is a no-op (see
    /// <see cref="PRFactoryAuthorityStore.Set"/>). Jobs, worktrees, artefacts and logs are untouched; only the
    /// ownership and workspace identity rows that block a fresh re-claim go.
    /// </summary>
    public PRFactoryReleaseResult ReleaseFenced(string server, Guid id)
    {
        using var connection = database.OpenConnection();
        Exec(connection, "BEGIN IMMEDIATE", null);
        try
        {
            using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT acceptance_state, state FROM prfactory_teams WHERE server=$server AND work_item_id=$id";
                read.Parameters.AddWithValue("$server", server);
                read.Parameters.AddWithValue("$id", id.ToString("D"));
                using var reader = read.ExecuteReader();
                if (!reader.Read()) { Exec(connection, "ROLLBACK", null); return PRFactoryReleaseResult.NotFound; }
                if (reader.GetString(0) != "reconciliation_needed" || reader.GetString(1) != "claimed")
                {
                    Exec(connection, "ROLLBACK", null);
                    return PRFactoryReleaseResult.NotFenced;
                }
            }
            // Ownership may only go once the daemon durably fenced the item AND acknowledged the stop of every owned
            // turn, retained session and external member (stopping=0 on a non-accepted authority row). A missing or
            // accepted row is the SetAcceptance-to-Observe gap: fail closed.
            var blocked = Blocker(connection, server, id);
            if (blocked is not null)
            {
                Exec(connection, "ROLLBACK", null);
                return blocked.Value;
            }
            foreach (var table in new[] { "prfactory_pending_commands", "prfactory_authority", "prfactory_members", "prfactory_external", "prfactory_command_receipts",
                "prfactory_artefact_delivery", "prfactory_human_stream", "human_waits", "prfactory_killed_members", "prfactory_teams" })
            {
                Exec(connection, $"DELETE FROM {table} WHERE server=$server AND work_item_id=$id", (server, id));
            }
            // The re-claim must prepare a fresh workspace: the kept one may hold the old run's uncommitted output,
            // which the phase-start base refresh refuses forever. Its files stay on disk; only the identity rows go
            // (secondary repositories are keyed "<key>|<repository>").
            foreach (var table in new[] { "prfactory_workspace_integrations", "prfactory_base_refresh", "prfactory_wip",
                "prfactory_wip_releases", "prfactory_multi_refresh", "prfactory_repository_sets", "prfactory_workspaces" })
            {
                Exec(connection, $"DELETE FROM {table} WHERE workspace_key=$server||'|'||$id OR substr(workspace_key,1,length($server||'|'||$id)+1)=$server||'|'||$id||'|'", (server, id));
            }
            Exec(connection, "COMMIT", null);
            return PRFactoryReleaseResult.Released;
        }
        catch
        {
            try { Exec(connection, "ROLLBACK", null); } catch (Microsoft.Data.Sqlite.SqliteException) { }
            throw;
        }
    }

    static PRFactoryReleaseResult? Blocker(Microsoft.Data.Sqlite.SqliteConnection connection, string server, Guid id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE owned(job_id) AS (
                SELECT job_id FROM prfactory_members WHERE server=$server AND work_item_id=$id
                UNION
                SELECT j.job_id FROM jobs j JOIN owned o ON j.parent_job_id=o.job_id)
            SELECT
                NOT EXISTS(SELECT 1 FROM prfactory_authority WHERE server=$server AND work_item_id=$id
                    AND disposition<>'accepted' AND stopping=0)
                OR EXISTS(SELECT 1 FROM jobs j JOIN owned o ON o.job_id=j.job_id WHERE j.status IN ('queued','running')),
                EXISTS(SELECT 1 FROM jobs j JOIN owned o ON o.job_id=j.job_id WHERE j.status='needs_reconciliation'),
                EXISTS(SELECT 1 FROM prfactory_external WHERE server=$server AND work_item_id=$id AND closed=0)
            """;
        command.Parameters.AddWithValue("$server", server);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        reader.Read();
        return reader.GetInt32(0) != 0 ? PRFactoryReleaseResult.StopNotAcknowledged
            : reader.GetInt32(1) != 0 ? PRFactoryReleaseResult.NeedsReconciliation
            : reader.GetInt32(2) != 0 ? PRFactoryReleaseResult.ExternalMemberOpen : null;
    }

    static void Exec(Microsoft.Data.Sqlite.SqliteConnection connection, string sql, (string Server, Guid Id)? key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (key is { } k)
        {
            command.Parameters.AddWithValue("$server", k.Server);
            command.Parameters.AddWithValue("$id", k.Id.ToString("D"));
        }
        command.ExecuteNonQuery();
    }
}
