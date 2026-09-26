using System.Runtime.InteropServices;

namespace AgentTeamForge.Host.Hosting;

internal static partial class WindowsCommandLine
{
    public static string[]? Split(string commandLine)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var vector = CommandLineToArgvW(commandLine, out var count);
        if (vector == 0 || count <= 0)
        {
            return null;
        }

        try
        {
            var args = new string[count];
            for (var i = 0; i < count; i++)
            {
                args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(vector, i * IntPtr.Size)) ?? "";
            }
            return args;
        }
        finally { _ = LocalFree(vector); }
    }

    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CommandLineToArgvW(string commandLine, out int argumentCount);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
