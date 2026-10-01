using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>Unix (Linux and macOS) restart cleanup for backend processes started by an earlier daemon.</summary>
public static class OrphanedBackendProcess
{
    const string Marker = "ATF_RUN_CORRELATION";
    const int SigKill = 9;

    public static void Mark(ProcessStartInfo info, string correlation) => info.Environment[Marker] = correlation;

    /// <summary>
    /// A Codex <c>-c</c> override that puts the marker into its tool shells. Codex builds their environment from
    /// <c>shell_environment_policy</c> (for example <c>inherit = "core"</c>), which drops the inherited marker, so
    /// without it a shell the tool started (<c>setsid</c>, own session) is invisible to restart cleanup.
    /// </summary>
    public static string CodexShellMarker(string correlation) =>
        $"shell_environment_policy.set.{Marker}=" + JsonValue.Create(correlation).ToJsonString();

    /// <summary>Read-only Unix check used before reviving an uncertain native session.</summary>
    public static bool HasMarkedProcess(IReadOnlyCollection<string> correlations,
        IReadOnlyCollection<int>? knownPids = null, Func<int, byte[]>? readEnvironment = null)
    {
        if (OperatingSystem.IsMacOS())
        {
            return HasMarkedDarwinProcess(correlations, knownPids ?? []);
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
    /// alone may have been recycled, so on Linux each process is pinned with a
    /// pidfd before its environment is checked and is signalled through that
    /// pidfd; Darwin pins by creation token and walks the tree instead, and also
    /// signals the run's recorded descendants (<see cref="RunProcessSnapshots"/>).
    /// </summary>
    public static int TerminateMarked(IReadOnlyCollection<string> correlations)
    {
        if (OperatingSystem.IsMacOS())
        {
            // The live tree first; then recorded members whose marked ancestors are already gone.
            return TerminateMarkedDarwinTree(correlations) + RunProcessSnapshots.Kill(correlations);
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

    enum DarwinMark { Marked, Unmarked, Hidden, Unreadable }

    /// <summary>
    /// kern.procargs2 omits the environment of Apple platform binaries (/bin/sh, /bin/sleep, ...)
    /// even for the caller's own processes, so only third-party processes such as the agent CLIs
    /// reveal a marker. Every non-empty environment is complete, so it proves presence or absence.
    /// </summary>
    static DarwinMark ReadDarwinMark(int pid, HashSet<string> markers) => DarwinProcess.Arguments(pid) switch
    {
        null => DarwinMark.Unreadable,
        { Environment.Length: 0 } => DarwinMark.Hidden,
        var (_, environment) when environment.Any(markers.Contains) => DarwinMark.Marked,
        _ => DarwinMark.Unmarked,
    };

    /// <summary>Whether a Darwin process visibly carries this run marker (platform binaries never do).</summary>
    internal static bool VisiblyMarked(int pid, string marker) => ReadDarwinMark(pid, DarwinMarkers([marker])) == DarwinMark.Marked;

    static HashSet<string> DarwinMarkers(IReadOnlyCollection<string> correlations) =>
        correlations.Select(c => $"{Marker}={c}").ToHashSet(StringComparer.Ordinal);

    static bool HasMarkedDarwinProcess(IReadOnlyCollection<string> correlations, IReadOnlyCollection<int> knownPids)
    {
        if (correlations.Count == 0)
        {
            return false;
        }

        var live = DarwinProcess.Table().Where(e => !e.IsZombie).Select(e => e.Pid).ToHashSet();
        if (live.Count == 0)
        {
            return true; // The process table is unreadable: no proof.
        }

        var markers = DarwinMarkers(correlations);
        // As on Linux, a previously owned PID whose identity cannot be read may still be the launch;
        // on Darwin that includes a platform binary, whose environment is never visible.
        return knownPids.Any(pid => live.Contains(pid) && ReadDarwinMark(pid, markers) != DarwinMark.Unmarked)
            || live.Any(pid => ReadDarwinMark(pid, markers) == DarwinMark.Marked);
    }

    /// <summary>
    /// Darwin cannot rely on descendants showing the inherited marker (see <see cref="ReadDarwinMark"/>), so
    /// it kills each visibly marked process together with its descendant tree. The tree is captured, with
    /// creation tokens, before any signal: a killed parent's children are reparented to launchd. A child
    /// joins only while its kernel-reported parent is still the captured one, and the daemon's own subtree
    /// is never entered.
    /// </summary>
    static int TerminateMarkedDarwinTree(IReadOnlyCollection<string> correlations)
    {
        if (correlations.Count == 0)
        {
            return 0;
        }

        var self = Environment.ProcessId;
        var table = DarwinProcess.Table().Where(e => !e.IsZombie && e.Pid != self).ToList();
        var children = table.ToLookup(e => e.ParentPid, e => e.Pid);
        var markers = DarwinMarkers(correlations);
        var pending = new Queue<DarwinProcess.Entry>();
        foreach (var entry in table)
        {
            // Pin before reading the marker: the signal-time token recheck then proves the marker was this process's.
            if (DarwinProcess.Info(entry.Pid) is { Token: not null, IsZombie: false } pinned && ReadDarwinMark(entry.Pid, markers) == DarwinMark.Marked)
            {
                pending.Enqueue(pinned);
            }
        }

        var targets = new Dictionary<int, ulong>();
        while (pending.TryDequeue(out var parent))
        {
            if (!targets.TryAdd(parent.Pid, parent.Token!.Value))
            {
                continue;
            }

            foreach (var child in children[parent.Pid])
            {
                if (DarwinProcess.Info(child) is { Token: not null, IsZombie: false } pinned && pinned.ParentPid == parent.Pid)
                {
                    pending.Enqueue(pinned);
                }
            }
        }

        return targets.Count(t => DarwinProcess.SignalIfSame(t.Key, t.Value, SigKill));
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
