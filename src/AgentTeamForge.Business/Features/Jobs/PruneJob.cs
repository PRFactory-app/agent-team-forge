using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Business.Features.Jobs;

public sealed class PruneJob(PruneJobs jobs, string stateDirectory)
{
    public int Execute(int olderThanDays, bool dryRun)
    {
        if (olderThanDays is < 1 or > 36500)
        {
            throw new ArgumentOutOfRangeException(nameof(olderThanDays));
        }
        var logs = Path.Combine(stateDirectory, "logs");
        if (Directory.Exists(logs) && new DirectoryInfo(logs).LinkTarget is not null)
        {
            throw new IOException("Job log directory is a symlink");
        }

        var ids = jobs.Execute(DateTimeOffset.UtcNow.AddDays(-olderThanDays), dryRun);
        if (!dryRun && Directory.Exists(logs))
        {
            foreach (var id in ids.Where(id => Path.GetFileName(id) == id))
            {
                File.Delete(Path.Combine(logs, id + ".log"));
            }
        }
        return ids.Count;
    }
}
