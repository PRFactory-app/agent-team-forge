using System.Globalization;
using System.Text;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Host.Hosting;

/// <summary>Best-effort, once-per-boot backup before the daemon opens the database.</summary>
internal static class StartupBackup
{
    internal static void Run(StateDirectory state, TimeSpan busyTimeout, Action<string> log, Func<string>? bootId = null)
    {
        try
        {
            if (!File.Exists(state.Database))
            {
                return;
            }

            var currentBoot = (bootId ?? CurrentBootId)();
            var backups = Path.Combine(state.Path, "backups");
            Directory.CreateDirectory(backups, StateDirectory.PrivateDir);
            var info = new DirectoryInfo(backups);
            if (info.LinkTarget is not null || info.UnixFileMode != StateDirectory.PrivateDir)
            {
                throw new IOException("backup directory is not private");
            }

            var marker = Path.Combine(backups, "boot-id");
            if (File.Exists(marker) && Encoding.UTF8.GetString(StateDirectory.ReadPrivateFile(marker)).Trim() == currentBoot)
            {
                return;
            }

            var name = $"jobs-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.db";
            var backup = Path.Combine(backups, name);
            try
            {
                JobDatabase.Backup(state.Database, backup, busyTimeout);
                File.SetUnixFileMode(backup, StateDirectory.PrivateFile);
            }
            catch
            {
                File.Delete(backup);
                throw;
            }

            foreach (var old in Directory.GetFiles(backups, "jobs-*.db").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(2))
            {
                File.Delete(old);
            }

            var temporaryMarker = Path.Combine(backups, $"boot-id-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporaryMarker, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = StateDirectory.PrivateFile,
                }))
                {
                    stream.Write(Encoding.UTF8.GetBytes(currentBoot));
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryMarker, marker, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryMarker);
            }

            log($"backup created: {name}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log($"backup failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    static string CurrentBootId()
    {
        if (OperatingSystem.IsLinux())
        {
            return File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        }

        // Untested on Windows/macOS: one backup per UTC day instead of a boot id.
        return DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
