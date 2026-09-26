using System.Runtime.InteropServices;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace AgentTeamForge.Host.Transport;

/// <summary>Named-pipe presence, so a client fails fast like a refused Unix socket.</summary>
internal static partial class WindowsPipe
{
    const int ErrorFileNotFound = 2;

    internal const string AccessDeniedMessage = "Access to the daemon pipe was denied. Run the daemon and client as the same Windows user. If an older daemon is running, stop and restart it with the updated atf binary.";

    [SupportedOSPlatform("windows")]
    internal static NamedPipeServerStream CreateServer(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("Cannot determine the current Windows user SID.");
        // Elevated tokens can default to BUILTIN\Administrators as owner. Use the
        // user SID so the identity remains stable across elevation levels.
        // Set the medium label atomically with the user-only DACL. PipeSecurity
        // cannot preserve this mandatory-label ACE as an audit rule, and its
        // WRITE_OWNER request aliases FILE_FLAG_FIRST_PIPE_INSTANCE, which would
        // block later instances. Squatting is refused by the ACL check below.
        var sddl = $"O:{user.Value}D:P(A;;0x001F019F;;;{user.Value})S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        SafePipeHandle handle;
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            // PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED; byte mode with
            // PIPE_REJECT_REMOTE_CLIENTS, unlimited instances.
            handle = CreateNamedPipe(@"\\.\pipe\" + name, 3 | 0x40000000, 8, 255, 0, 0, 0, ref attributes);
        }
        finally { LocalFree(descriptor); }
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        NamedPipeServerStream pipe;
        try { pipe = new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle); }
        catch { handle.Dispose(); throw; }
        try
        {
            // Windows ignores the supplied descriptor when joining an existing
            // pipe name. Refuse an instance whose existing ACL is not private.
            var actual = pipe.GetAccessControl();
            var rules = actual.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
            if (!user.Equals(actual.GetOwner(typeof(SecurityIdentifier))) || !actual.AreAccessRulesProtected
                || rules.Length != 1 || !user.Equals(rules[0].IdentityReference)
                || rules[0].AccessControlType != AccessControlType.Allow || rules[0].PipeAccessRights != PipeAccessRights.FullControl)
            {
                throw new UnauthorizedAccessException("The existing daemon pipe is not private to the current Windows user.");
            }
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static async Task<NamedPipeClientStream> ConnectAsync(string name, CancellationToken cancellationToken)
    {
        // .NET CurrentUserOnly compares against the token's default Owner, which
        // can be Administrators when elevated. Validate the user SID ourselves,
        // on the connected handle and before the caller sends any credentials.
        var pipe = new NamedPipeClientStream(".", name, PipeAccessRights.ReadWrite | PipeAccessRights.ReadPermissions,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous, HandleInheritability.None);
        try
        {
            await pipe.ConnectAsync(cancellationToken);
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is null || !identity.User.Equals(pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier))))
            {
                throw new UnauthorizedAccessException(AccessDeniedMessage);
            }
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes
    {
        public int Length;
        public nint Descriptor;
        public int InheritHandle;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out nint descriptor, out uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes attributes);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

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
