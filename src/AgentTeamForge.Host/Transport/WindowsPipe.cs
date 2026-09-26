using System.Runtime.InteropServices;

namespace AgentTeamForge.Host.Transport;

/// <summary>Named-pipe presence, so a client fails fast like a refused Unix socket.</summary>
internal static partial class WindowsPipe
{
    const int ErrorFileNotFound = 2;

    /// <summary>
    /// False only when the pipe stays absent through a short grace: the daemon briefly
    /// has no listening instance between accepting one connection and creating the next.
    /// </summary>
    internal static async Task<bool> AppearsAsync(Func<bool> exists, TimeSpan grace, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + grace;
        while (!exists())
        {
            if (DateTime.UtcNow >= until)
            {
                return false;
            }
            await Task.Delay(25, cancellationToken);
        }
        return true;
    }

    /// <summary>Busy instances count as present; only "no such pipe" is absent.</summary>
    internal static bool Exists(string name) =>
        WaitNamedPipe(@"\\.\pipe\" + name, 1) || Marshal.GetLastPInvokeError() != ErrorFileNotFound;

    [LibraryImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WaitNamedPipe(string name, uint timeout);
}
