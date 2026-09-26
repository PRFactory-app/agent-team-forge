using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentTeamForge.Host.Features.Wake;

/// <summary>Find the nearest Claude, Codex or Pi host through a Toolhelp process snapshot.</summary>
internal static unsafe partial class WindowsHostAncestry
{
    internal sealed record Row(int Parent, string Name, string? CommandLine = null);

    internal static (int Pid, string Kind)? Resolve(int start, IReadOnlyDictionary<int, Row> rows)
    {
        var visited = new HashSet<int>();
        for (var pid = start; pid > 0 && visited.Add(pid) && visited.Count <= 64;)
        {
            if (!rows.TryGetValue(pid, out var row))
            {
                return null;
            }
            var name = Path.GetFileNameWithoutExtension(row.Name).ToLowerInvariant();
            var kind = name is "claude" or "codex" or "pi" ? name : null;
            if (kind is null && name is "node" or "nodejs" && row.CommandLine is { } command)
            {
                var line = command.Replace('\\', '/').ToLowerInvariant();
                kind = line.Contains("@anthropic-ai/claude-code", StringComparison.Ordinal) ? "claude"
                    : line.Contains("@openai/codex", StringComparison.Ordinal) ? "codex"
                    : line.Contains("@earendil-works/pi-coding-agent", StringComparison.Ordinal) ? "pi" : null;
            }
            if (kind is not null)
            {
                return (pid, kind);
            }
            pid = row.Parent;
        }
        return null;
    }

    internal static (int Pid, string Kind)? NearestHost()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        var rows = new Dictionary<int, Row>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot is -1 or 0)
        {
            return null;
        }
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    var name = entry.ExeFile;
                    rows[unchecked((int)entry.ProcessId)] = new Row(unchecked((int)entry.ParentProcessId), new string(name));
                }
                while (Process32Next(snapshot, ref entry));
            }
        }
        finally { CloseHandle(snapshot); }

        // CIM is needed only for Node shims; Toolhelp supplies the trusted PID/parent chain.
        var visited = new HashSet<int>();
        for (var pid = Environment.ProcessId; pid > 0 && visited.Add(pid) && visited.Count <= 64 && rows.TryGetValue(pid, out var row); pid = row.Parent)
        {
            if (Path.GetFileNameWithoutExtension(row.Name).ToLowerInvariant() is "node" or "nodejs")
            {
                rows[pid] = row with { CommandLine = CommandLine(pid) };
            }
        }
        return Resolve(Environment.ProcessId, rows);
    }

    static string? CommandLine(int pid)
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine");
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }
            var output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(5000) && process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { return null; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    unsafe struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeap;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        public fixed char ExeFile[260];
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateToolhelp32Snapshot", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32First(nint snapshot, ref ProcessEntry entry);
    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32Next(nint snapshot, ref ProcessEntry entry);
    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
