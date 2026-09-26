using System.Diagnostics;
using System.Text;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>Unix restart cleanup for backend processes started by an earlier daemon.</summary>
public static class OrphanedBackendProcess
{
    const string Marker = "ATF_RUN_CORRELATION";
    const int SigKill = 9;

    public static void Mark(ProcessStartInfo info, string correlation) => info.Environment[Marker] = correlation;

    /// <summary>Read-only Unix check used before reviving an uncertain native session.</summary>
    public static bool HasMarkedProcess(IReadOnlyCollection<string> correlations,
        IReadOnlyCollection<int>? knownPids = null, Func<int, byte[]>? readEnvironment = null)
    {
        if (OperatingSystem.IsMacOS())
        {
            if (correlations.Count == 0)
            {
                return false;
            }

            var darwinMarkers = correlations.Select(c => $"{Marker}={c}").ToHashSet(StringComparer.Ordinal);
            var pids = DarwinProcess.Pids();
            if (pids.Count == 0)
            {
                return true; // ps failed: no proof.
            }

            // kern.procargs2 fails only for other users' processes, which cannot carry our marker.
            return pids.Any(pid => DarwinProcess.Arguments(pid)?.Environment.Any(darwinMarkers.Contains) == true);
        }
        if (!OperatingSystem.IsLinux() || correlations.Count == 0)
        {
            return true; // No proof that an uncertain run is idle on this platform.
        }

        var markers = correlations.Select(c => Encoding.UTF8.GetBytes($"{Marker}={c}\0")).ToList();
        readEnvironment ??= pid => File.ReadAllBytes($"/proc/{pid}/environ");
        // Only previously owned PIDs warrant conservative treatment when identity
        // is unreadable. A readable environment without our random run marker
        // proves this is no longer that launch; unrelated inaccessible PIDs do not.
        foreach (var pid in knownPids ?? [])
        {
            try
            {
                var environment = readEnvironment(pid);
                if (markers.Any(marker => HasEntry(environment, marker)))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (MayBeAlive(pid)) { return true; }
            }
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), out var pid) || pid <= 0)
                {
                    continue;
                }

                try
                {
                    var environment = readEnvironment(pid);
                    if (markers.Any(marker => HasEntry(environment, marker)))
                    {
                        return true;
                    }
                }
                catch (FileNotFoundException) { } // Process exited during the scan.
                catch (DirectoryNotFoundException) { }
                catch (UnauthorizedAccessException) { } // Known owned PIDs were checked above.
                catch (IOException) { }
            }

            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    static bool MayBeAlive(int pid)
    {
        try
        {
            // Enumerate rather than inspect process metadata: /proc/<pid>/stat
            // may be unreadable too. Directory.Exists also hides access errors.
            return Directory.EnumerateDirectories("/proc", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // Failure to inspect is not proof of exit.
        }
    }

    /// <summary>
    /// Kills every process whose environment carries one of the given run
    /// markers: the backend child and descendants that inherited it. A PID
    /// alone may have been recycled, so each process is pinned with a pidfd
    /// before its environment is checked and is signalled through that pidfd.
    /// </summary>
    public static int TerminateMarked(IReadOnlyCollection<string> correlations)
    {
        if (OperatingSystem.IsMacOS())
        {
            var darwinMarkers = correlations.Select(c => $"{Marker}={c}").ToHashSet(StringComparer.Ordinal);
            var darwinKilled = 0;
            foreach (var pid in DarwinProcess.Pids())
            {
                if (pid == Environment.ProcessId)
                {
                    continue;
                }

                var token = DarwinProcess.CreationToken(pid);
                if (token is not null && DarwinProcess.Arguments(pid)?.Environment.Any(darwinMarkers.Contains) == true
                    && DarwinProcess.SignalIfSame(pid, token.Value, SigKill))
                {
                    darwinKilled++;
                }
            }
            return darwinKilled;
        }
        if (!OperatingSystem.IsLinux() || correlations.Count == 0)
        {
            return 0;
        }

        var markers = correlations.Select(c => Encoding.UTF8.GetBytes($"{Marker}={c}\0")).ToList();
        var self = Environment.ProcessId;
        var killed = 0;
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (int.TryParse(Path.GetFileName(directory), out var pid) && pid > 0 && pid != self
                && TryKill(pid, markers))
            {
                killed++;
            }
        }

        return killed;
    }

    static bool TryKill(int pid, List<byte[]> markers)
    {
        using var pidfd = Pidfd.Open(pid);
        if (pidfd is null)
        {
            return false;
        }

        byte[] environment;
        try
        {
            // If the pinned process died and the PID was reused before this
            // read, the signal below goes to the dead pidfd and is a no-op.
            environment = File.ReadAllBytes($"/proc/{pid}/environ");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (!markers.Any(marker => HasEntry(environment, marker)))
        {
            return false;
        }

        return Pidfd.Signal(pidfd, SigKill);
    }

    static bool HasEntry(ReadOnlySpan<byte> environment, ReadOnlySpan<byte> entry)
    {
        var offset = 0;
        while (offset < environment.Length)
        {
            var at = environment[offset..].IndexOf(entry);
            if (at < 0)
            {
                return false;
            }

            at += offset;
            if (at == 0 || environment[at - 1] == 0)
            {
                return true;
            }

            offset = at + 1;
        }

        return false;
    }
}
