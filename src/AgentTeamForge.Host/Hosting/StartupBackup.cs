using AgentTeamForge.DAL.Files;
using System.Globalization;
using System.Text;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Host.Hosting;

/// <summary>
/// Best-effort, once-per-boot backup before the daemon opens the database. The daemon verifies
/// the live database first; the copy is verified again before any older backup is rotated out,
/// so a damaged copy never replaces a good one.
/// </summary>
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
            var backups = BackupDirectory(state);
            StateDirectory.CreatePrivateDirectory(backups);
            var info = new DirectoryInfo(backups);
            if (info.LinkTarget is not null || !OperatingSystem.IsWindows() && info.UnixFileMode != StateDirectory.PrivateDir)
            {
                throw new IOException($"backup directory {backups} is not private; it must be a real directory with mode 0700 (chmod 700 \"{backups}\")");
            }
            if (OperatingSystem.IsWindows())
            {
                WindowsPrivatePaths.ValidateDirectory(backups);
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
                JobDatabase.Verify(backup, busyTimeout);
            }
            catch
            {
                File.Delete(backup);
                File.Delete(backup + "-wal");
                File.Delete(backup + "-shm");
                throw;
            }

            foreach (var old in Directory.GetFiles(backups, "jobs-*.db").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(2))
            {
                File.Delete(old);
            }

            var temporaryMarker = Path.Combine(backups, $"boot-id-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporaryMarker, PrivateFiles.Options(FileMode.CreateNew, FileAccess.Write)))
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

    internal static string BackupDirectory(StateDirectory state) => Path.Combine(state.Path, "backups");

    /// <summary>Newest first; the names sort by creation time.</summary>
    internal static string[] List(StateDirectory state)
    {
        var backups = BackupDirectory(state);
        return Directory.Exists(backups)
            ? [.. Directory.GetFiles(backups, "jobs-*.db").OrderByDescending(Path.GetFileName, StringComparer.Ordinal)]
            : [];
    }

    static string CurrentBootId()
    {
        if (OperatingSystem.IsLinux())
        {
            return File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        }

        if (OperatingSystem.IsMacOS() && Native.DarwinBootTime() is { } boot)
        {
            return "darwin-" + boot.ToString(CultureInfo.InvariantCulture);
        }

        // Fallback if boot identity is unavailable.
        return DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
