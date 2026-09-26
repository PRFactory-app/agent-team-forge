using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>A real console for an agent when WT cannot provide a usable tab.</summary>
internal sealed class WindowsConsoleProcess(nint handle) : IDisposable
{
    public bool HasExited => WindowsTabNative.GetExitCodeProcess(handle, out var code) && code != 259;
    public int ExitCode => WindowsTabNative.GetExitCodeProcess(handle, out var code) ? unchecked((int)code) : -1;

    public static unsafe WindowsConsoleProcess Start(string script)
    {
        var command = ("\"powershell.exe\" -NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"\0").ToCharArray();
        var startup = new WindowsTabNative.StartupInfo { Size = (uint)Marshal.SizeOf<WindowsTabNative.StartupInfo>() };
        fixed (char* text = command)
        {
            if (!WindowsTabNative.CreateProcess(null, text, 0, 0, false, 0x10, 0, null, ref startup, out var info))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CREATE_NEW_CONSOLE failed");
            }
            WindowsTabNative.CloseHandle(info.Thread);
            return new WindowsConsoleProcess(info.Process);
        }
    }

    public void Dispose() => WindowsTabNative.CloseHandle(handle);
}

/// <summary>Kill owned wrappers when the daemon exits; persisted sidecars cover abrupt restarts.</summary>
internal static class WindowsTabJob
{
    static readonly Lock Gate = new();
    static nint _job;

    public static unsafe void Assign(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        lock (Gate)
        {
            if (_job == 0)
            {
                var job = WindowsTabNative.CreateJobObject(0, null);
                if (job == 0)
                {
                    return;
                }
                Span<byte> limits = stackalloc byte[IntPtr.Size == 8 ? 144 : 112];
                limits.Clear();
                BitConverter.TryWriteBytes(limits.Slice(16, 4), 0x2000); // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                fixed (byte* data = limits)
                {
                    if (!WindowsTabNative.SetInformationJobObject(job, 9, data, (uint)limits.Length))
                    {
                        WindowsTabNative.CloseHandle(job);
                        return;
                    }
                }
                _job = job;
            }
            try
            {
                using var process = Process.GetProcessById(pid);
                _ = WindowsTabNative.AssignProcessToJobObject(_job, process.Handle);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
        }
    }
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

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateJobObject(nint attributes, string? name);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetInformationJobObject(nint job, int infoClass, byte* info, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "AssignProcessToJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
}
