using System.Diagnostics;

namespace AgentTeamForge.Tests.Support;

/// <summary>
/// Executables whose environment, and so whose run marker, other same-user processes can read.
/// macOS hides the environment of Apple platform binaries (/bin/sh, /bin/sleep) from kern.procargs2,
/// so a marked system tool cannot stand in for an agent CLI there; an ad-hoc signed copy, which is
/// not a platform binary, behaves like the real CLIs. Elsewhere the system tools are used as is.
/// </summary>
public static class MarkerVisibleExecutable
{
    // /bin/sh on macOS is a shim that re-executes the platform /bin/bash, so copy bash itself.
    static readonly Lazy<string> MacShell = new(() => AdHocCopy("/bin/bash"));
    static readonly Lazy<string> MacSleep = new(() => AdHocCopy("/bin/sleep"));

    public static string Shell => OperatingSystem.IsMacOS() ? MacShell.Value : "/bin/sh";

    public static string Sleep => OperatingSystem.IsMacOS() ? MacSleep.Value : "sleep";

    static string AdHocCopy(string source)
    {
        var directory = Directory.CreateTempSubdirectory("atf-visible-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Directory.Delete(directory, recursive: true);
        var path = Path.Combine(directory, Path.GetFileName(source));
        File.Copy(source, path);
        // A copied platform binary keeps Apple's signature and is killed at exec; re-sign it ad hoc.
        Codesign("--remove-signature", path);
        Codesign("--sign", "-", path);
        return path;
    }

    static void Codesign(params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/codesign", arguments)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("codesign did not start");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"codesign {string.Join(' ', arguments)} failed: {error}");
        }
    }
}
