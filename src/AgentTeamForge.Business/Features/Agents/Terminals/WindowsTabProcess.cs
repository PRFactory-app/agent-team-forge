using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>A Windows launcher that can break away from an ambient daemon job.</summary>
internal sealed class WindowsConsoleProcess(nint handle) : IDisposable
{
    internal const uint BreakawayFromJob = 0x01000000;
    internal const uint NewProcessGroup = 0x00000200;
    internal const uint NewConsole = 0x00000010;

    public bool HasExited => WindowsTabNative.GetExitCodeProcess(handle, out var code) && code != 259;
    public int ExitCode => WindowsTabNative.GetExitCodeProcess(handle, out var code) ? unchecked((int)code) : -1;

    internal static uint CreationFlags(bool newConsole) => NewProcessGroup | BreakawayFromJob | (newConsole ? NewConsole : 0);

    public static WindowsConsoleProcess StartConsole(string script) => Start("powershell.exe",
        ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script], newConsole: true);

    public static unsafe WindowsConsoleProcess Start(string executable, IEnumerable<string> arguments, bool newConsole)
    {
        var command = (WtTabControl.CommandLine([executable, .. arguments]) + "\0").ToCharArray();
        var startup = new WindowsTabNative.StartupInfo { Size = (uint)Marshal.SizeOf<WindowsTabNative.StartupInfo>() };
        fixed (char* text = command)
        {
            var flags = CreationFlags(newConsole);
            if (!WindowsTabNative.CreateProcess(null, text, 0, 0, false, flags, 0, null, ref startup, out var info))
            {
                var error = Marshal.GetLastPInvokeError();
                // Some ambient jobs deny breakaway. Match the reference's one retry.
                if (error != 5 || !WindowsTabNative.CreateProcess(null, text, 0, 0, false,
                    flags & ~BreakawayFromJob, 0, null, ref startup, out info))
                {
                    throw new Win32Exception(error == 5 ? Marshal.GetLastPInvokeError() : error, "interactive launcher failed");
                }
            }
            WindowsTabNative.CloseHandle(info.Thread);
            return new WindowsConsoleProcess(info.Process);
        }
    }

    public void Dispose() => WindowsTabNative.CloseHandle(handle);
}

internal static unsafe partial class WindowsTabNative
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        public uint Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2;
        public nint Reserved2Pointer;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcess(string? application, char* command, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? directory,
        ref StartupInfo startup, out ProcessInformation info);

    [LibraryImport("kernel32.dll", EntryPoint = "GetExitCodeProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(nint process, out uint code);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
}
