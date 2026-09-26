using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>Darwin process facts. An unreadable start time never proves ownership.</summary>
public static partial class DarwinProcess
{
    const int CtlKern = 1, KernProc = 14, KernProcPid = 1, KernProcArgs2 = 49;

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

    public static ulong? CreationToken(int pid)
    {
        if (!OperatingSystem.IsMacOS() || pid <= 0)
        {
            return null;
        }

        var raw = ReadSysctl([CtlKern, KernProc, KernProcPid, pid]);
        return raw is null ? null : ParseKinfoStartTime(raw);
    }

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

    public static IReadOnlyList<int> Pids()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return [];
        }

        try
        {
            var info = new ProcessStartInfo("/bin/ps") { UseShellExecute = false, RedirectStandardOutput = true };
            info.ArgumentList.Add("-axo");
            info.ArgumentList.Add("pid=");
            using var process = Process.Start(info);
            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000) || process.ExitCode != 0)
            {
                return [];
            }

            return [.. output.Split('\n').Select(s => int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : 0)
                .Where(pid => pid > 0)];
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return []; }
    }

    public static int? ParentPid(int pid)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        try
        {
            var info = new ProcessStartInfo("/bin/ps") { UseShellExecute = false, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-p", pid.ToString(CultureInfo.InvariantCulture), "-o", "ppid=" })
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(5000) && process.ExitCode == 0 && int.TryParse(output.Trim(), out var parent) ? parent : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

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
        if (Sysctl(mib, (uint)mib.Length, null, ref size, 0, 0) != 0 || size is 0 or > 4_194_304)
        {
            return null;
        }

        var raw = new byte[(int)size];
        var capacity = size;
        return Sysctl(mib, (uint)mib.Length, raw, ref size, 0, 0) == 0 && size <= capacity ? raw[..(int)size] : null;
    }

    [LibraryImport("libc", EntryPoint = "sysctl", SetLastError = true)]
    private static partial int Sysctl([In] int[] mib, uint count, [Out] byte[]? value, ref nuint size, nint replacement, nuint replacementSize);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
}
