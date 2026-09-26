using AgentTeamForge.DAL.Sqlite;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.DAL.Features.Jobs;

/// <summary>Removes expired terminal jobs, children before parents, in one transaction.</summary>
public sealed class PruneJobs(JobDatabase database)
{
    public IReadOnlyList<string> Execute(DateTimeOffset cutoff, bool dryRun)
    {
        try
        {
            using var connection = database.OpenConnection();
            using var tx = connection.BeginTransaction(deferred: false);
            var rows = new List<(string Id, string? Parent, bool Expired)>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = tx;
                // A result the lead has not read yet (unread wake notice) is never pruned.
                select.CommandText = """
                    SELECT j.job_id, j.parent_job_id, j.status, j.updated_at,
                           EXISTS (SELECT 1 FROM wake_jobs w WHERE w.job_id=j.job_id AND w.read_at IS NULL)
                    FROM jobs j
                    """;
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var status = reader.GetString(2);
                    var expired = reader.GetInt64(4) == 0 && status is "completed" or "failed" or "cancelled"
                        && DateTimeOffset.TryParse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var updated) && updated < cutoff;
                    rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), expired));
                }
            }

            var parentById = rows.ToDictionary(row => row.Id, row => row.Parent, StringComparer.Ordinal);
            var retained = rows.Where(row => !row.Expired).Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
            // A retained descendant pins every ancestor, including an old terminal parent.
            var keepQueue = new Queue<string>(retained);
            while (keepQueue.TryDequeue(out var keepId))
            {
                if (parentById[keepId] is { } parent && retained.Add(parent))
                {
                    keepQueue.Enqueue(parent);
                }
            }

            var pending = rows.Where(row => !retained.Contains(row.Id)).ToDictionary(row => row.Id, row => row.Parent, StringComparer.Ordinal);
            var childCounts = pending.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
            foreach (var parent in pending.Values)
            {
                if (parent is not null && childCounts.TryGetValue(parent, out var count))
                {
                    childCounts[parent] = count + 1;
                }
            }
            var leaves = new Queue<string>(childCounts.Where(row => row.Value == 0).Select(row => row.Key));
            var deleted = new List<string>(pending.Count);
            while (leaves.TryDequeue(out var id))
            {
                if (!dryRun)
                {
                    foreach (var table in new[] { "wake_jobs", "events", "runs", "dispatch_intents", "jobs" })
                    {
                        using var delete = connection.CreateCommand();
                        delete.Transaction = tx;
                        delete.CommandText = $"DELETE FROM {table} WHERE job_id=$id";
                        delete.Parameters.AddWithValue("$id", id);
                        delete.ExecuteNonQuery();
                    }
                }
                deleted.Add(id);
                if (pending[id] is { } parent && childCounts.TryGetValue(parent, out var count))
                {
                    childCounts[parent] = count - 1;
                    if (count == 1)
                    {
                        leaves.Enqueue(parent);
                    }
                }
            }

            if (deleted.Count != pending.Count)
            {
                throw new StorageException(StorageFailure.Unavailable, "job parent cycle");
            }
            if (!dryRun)
            {
                tx.Commit();
            }
            return deleted;
        }
        catch (SqliteException ex)
        {
            throw StorageException.From(ex);
        }
    }
}
