using System.Globalization;
using System.Text;
using AgentTeamForge.DAL.Files;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>
/// The process a run's descendants hang from and the run marker that names them. <c>Token</c> is the root's known
/// Darwin creation token; without one the root must visibly carry the marker.
/// </summary>
public readonly record struct ProcessSnapshotRoot(int Pid, string Marker, ulong? Token = null);

/// <summary>
/// macOS hides the environment of Apple platform binaries (tool shells, /bin/sleep) even from their owner, so a
/// run's process whose marked ancestors have all exited carries no visible marker and is reparented to launchd.
/// The daemon therefore records each running run's descendants (PID + creation token) in the state directory,
/// keeping members that have since been reparented away, and <see cref="OrphanedBackendProcess.TerminateMarked"/>
/// also signals recorded members, each only while it still has its recorded creation token. Linux reads every
/// process environment, so it needs none of this.
/// </summary>
public static class RunProcessSnapshots
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    const int SigKill = 9;
    const int MaxFileBytes = 1 << 20;
    static string? s_directory;

    /// <summary>Set once by the daemon before recovery; until then snapshots are neither written nor used.</summary>
    public static void Configure(string stateRoot) => Volatile.Write(ref s_directory, Path.Combine(stateRoot, "run-processes"));

    static string? Directory => OperatingSystem.IsMacOS() ? Volatile.Read(ref s_directory) : null;

    /// <summary>Records the current descendants of every live, verified root (one process-table read for all).</summary>
    public static void Capture(IReadOnlyCollection<ProcessSnapshotRoot> roots)
    {
        if (roots.Count > 0 && Directory is { } directory)
        {
            Capture(directory, roots, DarwinProcess.Table());
        }
    }

    internal static void Capture(string directory, IReadOnlyCollection<ProcessSnapshotRoot> roots, IReadOnlyList<DarwinProcess.Entry> table)
    {
        var live = new Dictionary<int, DarwinProcess.Entry>();
        foreach (var entry in table)
        {
            if (!entry.IsZombie && entry.Token is not null) { live[entry.Pid] = entry; }
        }
        if (live.Count == 0) { return; } // An unreadable table proves nothing.
        var children = live.Values.ToLookup(e => e.ParentPid);
        foreach (var root in roots)
        {
            if (!ValidMarker(root.Marker)) { continue; }
            var path = Path.Combine(directory, root.Marker);
            var previous = Read(path);
            if (!live.TryGetValue(root.Pid, out var pinned) || !RootVerified(root, pinned, previous)) { continue; }

            // Members recorded earlier stay while alive with the same token: they may have been reparented away.
            var members = new Dictionary<int, ulong>();
            foreach (var (pid, token) in previous?.Members ?? new Dictionary<int, ulong>())
            {
                if (live.TryGetValue(pid, out var entry) && entry.Token == token) { members[pid] = token; }
            }
            var visited = new HashSet<int>();
            var pending = new Queue<DarwinProcess.Entry>([pinned]);
            while (pending.TryDequeue(out var parent))
            {
                if (!visited.Add(parent.Pid)) { continue; }
                members[parent.Pid] = parent.Token!.Value;
                foreach (var child in children[parent.Pid]) { pending.Enqueue(child); }
            }
            members.Remove(Environment.ProcessId);
            var snapshot = new Snapshot(pinned.Pid, pinned.Token!.Value, members);
            if (previous is not null && previous.SameAs(snapshot)) { continue; }
            try { Write(directory, path, snapshot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // The next capture retries.
        }
    }

    /// <summary>
    /// The first capture pins the root's token: the caller's known token, or else a visible marker (read between two
    /// identical token reads, so it belongs to this process). Later captures only accept the pinned token.
    /// </summary>
    static bool RootVerified(ProcessSnapshotRoot root, DarwinProcess.Entry entry, Snapshot? previous)
    {
        if (previous is not null) { return previous.RootPid == entry.Pid && previous.RootToken == entry.Token; }
        if (root.Token is { } known) { return known == entry.Token; }
        return OrphanedBackendProcess.VisiblyMarked(entry.Pid, root.Marker) && DarwinProcess.CreationToken(entry.Pid) == entry.Token;
    }

    /// <summary>Signals every recorded member of these runs that still has its recorded token, then drops the records.</summary>
    public static int Kill(IReadOnlyCollection<string> markers) => Directory is { } directory ? Kill(directory, markers) : 0;

    internal static int Kill(string directory, IReadOnlyCollection<string> markers)
    {
        var killed = 0;
        foreach (var marker in markers.Where(ValidMarker))
        {
            var path = Path.Combine(directory, marker);
            if (Read(path) is not { } snapshot) { continue; }
            killed += Signal(snapshot.Members);
            Delete(path);
        }
        return killed;
    }

    /// <summary>Records whose members have all exited or been replaced; nothing in them can be signalled.</summary>
    public static void PruneExited()
    {
        if (Directory is not { } directory || !System.IO.Directory.Exists(directory)) { return; }
        foreach (var path in System.IO.Directory.EnumerateFiles(directory))
        {
            if (ValidMarker(Path.GetFileName(path)) && Read(path) is { } snapshot
                && snapshot.Members.All(m => DarwinProcess.CreationToken(m.Key) != m.Value))
            {
                Delete(path);
            }
        }
    }

    /// <summary>The live descendants of a root whose token is still <paramref name="token"/>, root included.</summary>
    public static IReadOnlyDictionary<int, ulong> Tree(int pid, ulong token)
    {
        var table = DarwinProcess.Table();
        if (!table.Any(e => e.Pid == pid && e.Token == token && !e.IsZombie)) { return new Dictionary<int, ulong>(); }
        var children = table.Where(e => !e.IsZombie && e.Token is not null).ToLookup(e => e.ParentPid);
        var tree = new Dictionary<int, ulong> { [pid] = token };
        var pending = new Queue<int>([pid]);
        while (pending.TryDequeue(out var parent))
        {
            foreach (var child in children[parent])
            {
                if (child.Pid != Environment.ProcessId && tree.TryAdd(child.Pid, child.Token!.Value)) { pending.Enqueue(child.Pid); }
            }
        }
        return tree;
    }

    /// <summary>SIGKILL for each process still carrying its recorded token; the token is rechecked at signal time.</summary>
    public static int Signal(IReadOnlyDictionary<int, ulong> members) =>
        members.Count(m => m.Key != Environment.ProcessId && DarwinProcess.SignalIfSame(m.Key, m.Value, SigKill));

    static bool ValidMarker(string marker) =>
        marker.Length is > 0 and <= 64 && marker.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9');

    sealed record Snapshot(int RootPid, ulong RootToken, IReadOnlyDictionary<int, ulong> Members)
    {
        public bool SameAs(Snapshot other) => RootPid == other.RootPid && RootToken == other.RootToken
            && Members.Count == other.Members.Count && Members.All(m => other.Members.TryGetValue(m.Key, out var t) && t == m.Value);
    }

    static Snapshot? Read(string path)
    {
        string[] lines;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.LinkTarget is not null || file.Length > MaxFileBytes) { return null; }
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (lines.Length == 0 || Pair(lines[0]) is not { } root) { return null; }
        var members = new Dictionary<int, ulong>();
        foreach (var line in lines.Skip(1))
        {
            if (Pair(line) is not { } member) { return null; }
            members[member.Pid] = member.Token;
        }
        return new(root.Pid, root.Token, members);
    }

    static (int Pid, ulong Token)? Pair(string line) =>
        line.Split(' ') is [var pid, var token]
            && int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p > 0
            && ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t > 0
            ? (p, t) : null;

    static void Write(string directory, string path, Snapshot snapshot)
    {
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"{snapshot.RootPid} {snapshot.RootToken}\n");
        foreach (var (pid, token) in snapshot.Members) { text.Append(CultureInfo.InvariantCulture, $"{pid} {token}\n"); }
        PrivateFiles.CreateDirectory(directory);
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, PrivateFiles.Options(FileMode.Create, FileAccess.Write)))
        {
            file.Write(Encoding.ASCII.GetBytes(text.ToString()));
        }
        File.Move(temporary, path, overwrite: true);
    }

    static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
