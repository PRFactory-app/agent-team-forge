using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AgentTeamForge.Host.Features.Setup;

// CreateProcess with inheritHandles=false keeps an MCP bridge's pipes out of
// the long-lived child. The child opens daemon.log itself after startup.
[SupportedOSPlatform("windows")]
internal static class WindowsDaemonLauncher
{
    const uint DetachedProcess = 0x00000008;
    const uint CreateNewProcessGroup = 0x00000200;
    const uint CreateUnicodeEnvironment = 0x00000400;
    const uint CreateNoWindow = 0x08000000;

    internal static Process Start(string binary, string stateDir)
    {
        var environment = DaemonEnvironment.Current();
        environment["ATF_DAEMON_LOG"] = Path.Combine(stateDir, "daemon.log");
        var block = string.Join('\0', environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
        var blockPointer = Marshal.StringToHGlobalUni(block);
        try
        {
            var commandLine = Marshal.StringToHGlobalUni($"{Quote(binary)} daemon --state-dir {Quote(stateDir)}");
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            try
            {
                if (!CreateProcess(binary, commandLine, 0, 0, false,
                    DetachedProcess | CreateNewProcessGroup | CreateNoWindow | CreateUnicodeEnvironment,
                    blockPointer, null, ref startup, out var child))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                try { return Process.GetProcessById(child.ProcessId); }
                finally
                {
                    CloseHandle(child.Thread);
                    CloseHandle(child.Process);
                }
            }
            finally { Marshal.FreeHGlobal(commandLine); }
        }
        finally { Marshal.FreeHGlobal(blockPointer); }
    }

    internal static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public nint Reserved2Pointer;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateProcess(string applicationName, nint commandLine, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
        nint environment, string? currentDirectory, ref StartupInfo startup, out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(nint handle);
}
