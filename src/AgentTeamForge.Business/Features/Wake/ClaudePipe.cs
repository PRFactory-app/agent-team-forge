using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
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
    const string Administrators = "S-1-5-32-544";

    // Elevated servers default object ownership to Administrators; then the server process must run as us.
    internal static bool TrustedOwner(string? user, string? owner, string? serverUser) =>
        SameUser(user, owner) || (owner == Administrators && SameUser(user, serverUser));

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
            // The server must be the host and the pipe owned by us, or by Administrators (elevated
            // default owner) with the server process running as us. Anonymous SQOS prevents the
            // server from impersonating this client.
            using var pipe = new NamedPipeClientStream(".", name, Access, Options,
                TokenImpersonationLevel.Anonymous, HandleInheritability.None);
            await pipe.ConnectAsync(token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != hostPid)
            {
                return false;
            }
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User?.Value;
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value;
            var serverUser = SameUser(user, owner) || owner != Administrators ? null : ProcessUser(serverPid);
            if (!TrustedOwner(user, owner, serverUser)) { return false; }
            await pipe.WriteAsync(wire, token);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
        finally { active.TryRemove(name, out _); slots.Release(); }
    }

    // Token user SID of a process, or null on any failure.
    static string? ProcessUser(uint pid)
    {
        if (!OperatingSystem.IsWindows()) { return null; }
        using var process = new SafeProcessHandle(OpenProcess(ProcessQueryLimitedInformation, false, pid), true);
        if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var raw)) { return null; }
        using var tokenHandle = new SafeAccessTokenHandle(raw);
        if (tokenHandle.IsInvalid) { return null; }
        try
        {
            using var server = new WindowsIdentity(tokenHandle.DangerousGetHandle());
            return server.User?.Value;
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    const uint ProcessQueryLimitedInformation = 0x1000;
    const uint TokenQuery = 0x0008;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverPid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out nint token);
}
