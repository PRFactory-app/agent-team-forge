using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AgentTeamForge.Host.Hosting;

/// <summary>Give the current Windows user exclusive access to a new state tree.</summary>
internal static partial class WindowsPrivatePaths
{
    public static void ValidateDirectory(string path)
    {
        using var handle = OpenDirectory(path, 0x20000, 7, 0, 3, 0x02000000, 0);
        if (handle.IsInvalid)
        {
            throw new StateDirectoryException("state_dir_acl_failed");
        }
        try { Validate(handle); }
        catch (StateDirectoryException) { throw new StateDirectoryException("state_dir_acl_failed"); }
    }

    public static void Validate(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
        var sid = WindowsIdentity.GetCurrent().User ?? throw new StateDirectoryException("private_file_unsafe");
        var expected = new byte[sid.BinaryLength];
        sid.GetBinaryForm(expected, 0);
        if (GetSecurityInfo(handle, 1, 0x5, out var owner, out _, out var dacl, out _, out var descriptor) != 0)
        {
            throw new StateDirectoryException("private_file_unsafe");
        }
        try
        {
            if (owner == 0 || dacl == 0 || ReadSid(owner) is not { } actualOwner)
            {
                throw new StateDirectoryException("private_file_unsafe");
            }
            var count = (ushort)Marshal.ReadInt16(dacl, 4);
            var aces = new List<(byte Type, byte[] Sid)>();
            for (uint i = 0; i < count; i++)
            {
                if (!GetAce(dacl, i, out var ace) || ace == 0 || Marshal.ReadByte(ace) != 0 || ReadSid(ace + 8) is not { } aceSid)
                {
                    throw new StateDirectoryException("private_file_unsafe");
                }
                aces.Add((Marshal.ReadByte(ace), aceSid));
            }
            if (!IsPrivateAcl(expected, actualOwner, aces))
            {
                throw new StateDirectoryException("private_file_unsafe");
            }
        }
        finally { LocalFree(descriptor); }
    }

    /// <summary>BUILTIN\Administrators (S-1-5-32-544): the default owner of anything an elevated admin creates.</summary>
    internal static readonly byte[] Administrators = [1, 2, 0, 0, 0, 0, 0, 5, 32, 0, 0, 0, 32, 2, 0, 0];

    internal static bool IsPrivateAcl(byte[] expected, byte[] owner, IReadOnlyList<(byte Type, byte[] Sid)> aces) =>
        (owner.AsSpan().SequenceEqual(expected) || owner.AsSpan().SequenceEqual(Administrators)) && aces.Count > 0
        && aces.All(ace => ace.Type == 0 && ace.Sid.AsSpan().SequenceEqual(expected));

    static byte[]? ReadSid(nint actual)
    {
        var length = GetLengthSid(actual);
        if (length is < 8 or > 68)
        {
            return null;
        }
        var bytes = new byte[length];
        Marshal.Copy(actual, bytes, 0, bytes.Length);
        return bytes;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "GetSecurityInfo")]
    private static partial uint GetSecurityInfo(SafeFileHandle handle, uint objectType, uint information, out nint owner,
        out nint group, out nint dacl, out nint sacl, out nint descriptor);

    [LibraryImport("advapi32.dll", EntryPoint = "GetAce")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetAce(nint acl, uint index, out nint ace);

    [LibraryImport("advapi32.dll", EntryPoint = "GetLengthSid")]
    private static partial int GetLengthSid(nint sid);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle OpenDirectory(string path, uint access, uint share, nint security,
        uint creation, uint flags, nint template);

    public static void Protect(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new StateDirectoryException("state_dir_owner_unknown");
        // Reset any explicit ACEs on a caller-supplied empty directory before
        // removing inherited ACEs. New state files then inherit only this user.
        RunIcacls(path, "/reset");
        RunIcacls(path, "/inheritance:r", "/grant:r", $"*{sid}:(OI)(CI)F");
    }

    static void RunIcacls(string path, params string[] args)
    {
        var info = new ProcessStartInfo("icacls.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(path);
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info) ?? throw new StateDirectoryException("state_dir_acl_failed");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            process.Kill();
            throw new StateDirectoryException("state_dir_acl_failed");
        }
        _ = output.GetAwaiter().GetResult();
        _ = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new StateDirectoryException("state_dir_acl_failed");
        }
    }
}
