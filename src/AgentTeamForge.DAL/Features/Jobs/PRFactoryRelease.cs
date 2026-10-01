namespace AgentTeamForge.DAL.Features.Jobs;

public enum PRFactoryReleaseResult { Released, NotFound, NotFenced, StillRunning }

public sealed partial class PRFactoryTeamStore
{
    /// <summary>
    /// Forgets a fenced team so the next poll can claim the item afresh. One immediate transaction, so it is
    /// race-free with a running daemon: a later authority write for the released item is a no-op (see
    /// <see cref="PRFactoryAuthorityStore.Set"/>). Jobs, worktrees, artefacts and logs are untouched; only the
    /// ownership rows that block re-claim go.
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
            using (var running = connection.CreateCommand())
            {
                // Owned execution must be quiescent: the daemon's retried stop completes before a release.
                running.CommandText = """
                    WITH RECURSIVE owned(job_id) AS (
                        SELECT job_id FROM prfactory_members WHERE server=$server AND work_item_id=$id
                        UNION
                        SELECT j.job_id FROM jobs j JOIN owned o ON j.parent_job_id=o.job_id)
                    SELECT EXISTS(SELECT 1 FROM jobs j JOIN owned o ON o.job_id=j.job_id WHERE j.status IN ('queued','running'))
                        OR EXISTS(SELECT 1 FROM prfactory_authority WHERE server=$server AND work_item_id=$id AND stopping=1)
                    """;
                running.Parameters.AddWithValue("$server", server);
                running.Parameters.AddWithValue("$id", id.ToString("D"));
                if (Convert.ToInt32(running.ExecuteScalar()) != 0)
                {
                    Exec(connection, "ROLLBACK", null);
                    return PRFactoryReleaseResult.StillRunning;
                }
            }
            foreach (var table in new[] { "prfactory_authority", "prfactory_members", "prfactory_external", "prfactory_command_receipts",
                "prfactory_artefact_delivery", "prfactory_human_stream", "human_waits", "prfactory_killed_members", "prfactory_teams" })
            {
                Exec(connection, $"DELETE FROM {table} WHERE server=$server AND work_item_id=$id", (server, id));
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
