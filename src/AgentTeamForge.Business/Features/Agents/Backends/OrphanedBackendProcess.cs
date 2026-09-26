using System.Diagnostics;
using System.Text;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>Linux restart cleanup for backend processes started by an earlier daemon.</summary>
public static class OrphanedBackendProcess
{
    const string Marker = "ATF_RUN_CORRELATION";
    const int SigKill = 9;

    public static void Mark(ProcessStartInfo info, string correlation) => info.Environment[Marker] = correlation;

    /// <summary>
    /// Kills every process whose environment carries one of the given run
    /// markers: the backend child and descendants that inherited it. A PID
    /// alone may have been recycled, so each process is pinned with a pidfd
    /// before its environment is checked and is signalled through that pidfd.
    /// </summary>
    public static int TerminateMarked(IReadOnlyCollection<string> correlations)
    {
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
