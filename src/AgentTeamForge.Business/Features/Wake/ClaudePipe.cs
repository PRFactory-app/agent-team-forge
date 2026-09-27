using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AgentTeamForge.Business.Features.Wake;

/// <summary>Local, overlapped pipe I/O. The CLR owns cancellation and retains native buffers
/// through completion. A pending cancellation reserves the channel until it drains.</summary>
internal static partial class ClaudePipe
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> active = new(StringComparer.OrdinalIgnoreCase);
    static readonly SemaphoreSlim slots = new(8);
    internal const PipeOptions Options = PipeOptions.Asynchronous;
    internal const PipeAccessRights Access = PipeAccessRights.Write | PipeAccessRights.ReadPermissions;
    internal static bool SameUser(string? user, string? owner) => user is not null && user == owner;

    internal static async Task<bool> PostAsync(string address, int hostPid, byte[] wire, CancellationToken token)
    {
        var name = ClaudeChannel.PipeName(address);
        if (name is null || hostPid <= 0 || !slots.Wait(0, CancellationToken.None)) { return false; }
        if (!active.TryAdd(name, 0)) { slots.Release(); return false; }
        // The worker retains the stream/buffer/reservation even if cancellation completion stalls.
        var operation = WriteAsync(name, hostPid, wire, token);
        try { return await operation.WaitAsync(token); }
        catch (OperationCanceledException) { return false; }
    }

    static async Task<bool> WriteAsync(string name, int hostPid, byte[] wire, CancellationToken token)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) { return false; }
            // Compare the user SID, not the token's default owner (Administrators when elevated).
            // Anonymous SQOS prevents the server from impersonating this client.
            using var pipe = new NamedPipeClientStream(".", name, Access, Options,
                TokenImpersonationLevel.Anonymous, HandleInheritability.None);
            await pipe.ConnectAsync(token);
            using var identity = WindowsIdentity.GetCurrent();
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value;
            if (!SameUser(identity.User?.Value, owner)) { return false; }
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != hostPid)
            {
                return false;
            }
            await pipe.WriteAsync(wire, token);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
        finally { active.TryRemove(name, out _); slots.Release(); }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverPid);
}
