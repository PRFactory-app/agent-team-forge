using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>Darwin process facts. An unreadable start time never proves ownership.</summary>
public static partial class DarwinProcess
{
    const int CtlKern = 1, KernProc = 14, KernProcAll = 0, KernProcPid = 1, KernProcArgs2 = 49;

    // LP64 struct kinfo_proc (identical on arm64 and x86_64): kp_proc.p_starttime at 0,
    // kp_proc.p_stat at 36, kp_proc.p_pid at 40, kp_eproc.e_ppid at 560.
    const int KinfoSize = 648, StatOffset = 36, PidOffset = 40, ParentOffset = 560;
    const byte Zombie = 5; // SZOMB

    /// <summary>One process-table row; <see cref="Token"/> is null when the start time is unreadable.</summary>
    public readonly record struct Entry(int Pid, int ParentPid, ulong? Token, bool IsZombie);

    // Ported from win-agent-teams process_manager.py's Darwin creation token:
    // kp_proc.p_starttime is the first timeval in the LP64 little-endian kinfo_proc.
    internal static ulong? ParseKinfoStartTime(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 12)
        {
            return null;
        }

        var seconds = BinaryPrimitives.ReadInt64LittleEndian(raw);
        var microseconds = BinaryPrimitives.ReadInt32LittleEndian(raw[8..]);
        return seconds > 0 && microseconds is >= 0 and < 1_000_000
            ? ((ulong)seconds << 20) | (uint)microseconds : null;
    }

    internal static Entry? ParseKinfo(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < KinfoSize)
        {
            return null;
        }

        var pid = BinaryPrimitives.ReadInt32LittleEndian(raw[PidOffset..]);
        return pid <= 0 ? null : new Entry(pid, BinaryPrimitives.ReadInt32LittleEndian(raw[ParentOffset..]),
            ParseKinfoStartTime(raw), raw[StatOffset] == Zombie);
    }

    /// <summary>The process table from one kern.proc.all read; empty when it cannot be read.</summary>
    public static IReadOnlyList<Entry> Table()
    {
        if (!OperatingSystem.IsMacOS() || ReadSysctl([CtlKern, KernProc, KernProcAll, 0]) is not { } raw)
        {
            return [];
        }

        var entries = new List<Entry>(raw.Length / KinfoSize);
        for (var offset = 0; offset + KinfoSize <= raw.Length; offset += KinfoSize)
        {
            if (ParseKinfo(raw.AsSpan(offset, KinfoSize)) is { } entry)
            {
                entries.Add(entry);
            }
        }
        return entries;
    }

    public static Entry? Info(int pid) =>
        OperatingSystem.IsMacOS() && pid > 0 && ReadSysctl([CtlKern, KernProc, KernProcPid, pid]) is { } raw
            && ParseKinfo(raw) is { } entry && entry.Pid == pid ? entry : null;

    public static ulong? CreationToken(int pid) => Info(pid)?.Token;

    public static bool PidAlive(int pid) => pid > 0 && (Kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() == 1); // EPERM still proves the PID exists.

    /// <summary>Darwin kern.procargs2: argc, executable path, padding, argv, then environment.</summary>
    public static (string[] Args, string[] Environment)? Arguments(int pid)
    {
        if (!OperatingSystem.IsMacOS() || pid <= 0)
        {
            return null;
        }

        var raw = ReadSysctl([CtlKern, KernProcArgs2, pid]);
        if (raw is null || raw.Length < 5)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(raw);
        if (count is < 0 or > 4096)
        {
            return null;
        }

        var offset = 4;
        if (!SkipString(raw, ref offset))
        {
            return null; // executable path
        }

        while (offset < raw.Length && raw[offset] == 0)
        {
            offset++;
        }

        var args = new string[count];
        for (var i = 0; i < count; i++)
        {
            if (!ReadString(raw, ref offset, out args[i]))
            {
                return null;
            }
        }
        var environment = new List<string>();
        while (offset < raw.Length && raw[offset] != 0)
        {
            if (!ReadString(raw, ref offset, out var entry))
            {
                return null;
            }

            environment.Add(entry);
        }
        return (args, [.. environment]);
    }

    // struct proc_vnodepathinfo (PROC_PIDVNODEPATHINFO): the cwd's vnode_info (152 bytes) and
    // MAXPATHLEN path, then the same pair for the root directory.
    const int ProcPidVnodePathInfo = 9, VnodeInfoSize = 152, MaxPathLength = 1024;

    /// <summary>A process's current directory (Darwin has no /proc/PID/cwd); null when it cannot be read.</summary>
    public static string? WorkingDirectory(int pid)
    {
        if (!OperatingSystem.IsMacOS() || pid <= 0)
        {
            return null;
        }

        var raw = new byte[2 * (VnodeInfoSize + MaxPathLength)];
        if (ProcPidInfo(pid, ProcPidVnodePathInfo, 0, raw, raw.Length) != raw.Length)
        {
            return null;
        }

        var path = raw.AsSpan(VnodeInfoSize, MaxPathLength);
        var end = path.IndexOf((byte)0);
        return end > 0 ? Encoding.UTF8.GetString(path[..end]) : null;
    }

    public static IReadOnlyList<int> Pids() => [.. Table().Select(e => e.Pid)];

    public static int? ParentPid(int pid) => Info(pid)?.ParentPid;

    public static bool SignalIfSame(int pid, ulong token, int signal)
    {
        // A token check and kill(2) are not atomic on Darwin. Recheck immediately;
        // the small remaining reuse race is documented for the volunteer run.
        return CreationToken(pid) == token && Kill(pid, signal) == 0;
    }

    static bool SkipString(byte[] raw, ref int offset) => ReadString(raw, ref offset, out _);

    static bool ReadString(byte[] raw, ref int offset, out string value)
    {
        value = "";
        if (offset >= raw.Length)
        {
            return false;
        }

        var end = Array.IndexOf(raw, (byte)0, offset);
        if (end < 0)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(raw, offset, end - offset);
        offset = end + 1;
        return true;
    }

    static byte[]? ReadSysctl(int[] mib)
    {
        nuint size = 0;
        if (Sysctl(mib, (uint)mib.Length, null, ref size, 0, 0) != 0 || size is 0 or > 16_777_216)
        {
            return null;
        }

        // Headroom for a process table that grows between the size probe and the read.
        size += size / 8;
        var raw = new byte[(int)size];
        var capacity = size;
        return Sysctl(mib, (uint)mib.Length, raw, ref size, 0, 0) == 0 && size <= capacity ? raw[..(int)size] : null;
    }

    [LibraryImport("libc", EntryPoint = "sysctl", SetLastError = true)]
    private static partial int Sysctl([In] int[] mib, uint count, [Out] byte[]? value, ref nuint size, nint replacement, nuint replacementSize);

    [LibraryImport("libc", EntryPoint = "proc_pidinfo", SetLastError = true)]
    private static partial int ProcPidInfo(int pid, int flavor, ulong argument, [Out] byte[] buffer, int size);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
}
