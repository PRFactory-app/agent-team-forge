using System.Diagnostics;
using AgentTeamForge.Business.Features.Processes;

namespace AgentTeamForge.Host.Features.Setup;

internal static class SystemdUser
{
    static readonly string[] SessionKeys = ["WAYLAND_DISPLAY", "DISPLAY", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS"];

    // A lazily started daemon must not live in the caller's cgroup: tearing down a
    // terminal or desktop-app scope SIGKILLs everything in it, including the daemon.
    internal static bool ScopeAvailable(Func<string, string?> env, Func<string, string?> findExecutable) =>
        OperatingSystem.IsLinux()
        && findExecutable("systemd-run") is not null
        && env("XDG_RUNTIME_DIR") is { Length: > 0 } runtime
        && Directory.Exists(Path.Combine(runtime, "systemd"))
        && File.Exists(Path.Combine(runtime, "bus"));

    internal static IReadOnlyList<string> ScopePrefix(string stateDir) =>
    [
        "--user", "--scope", "--quiet", "--collect", "-p", "KillMode=process",
        "--unit=agentteamforge-daemon-" + Guid.NewGuid().ToString("N")[..8],
        "--description=AgentTeamForge daemon (" + stateDir + ")", "--",
    ];

    // `systemctl --user show-environment` prints KEY=VALUE lines; values with special
    // characters come quoted as $'...' and are skipped (display names never need them).
    internal static Dictionary<string, string> ParseSessionEnvironment(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) { continue; }
            var key = line[..separator];
            var value = line[(separator + 1)..].TrimEnd('\r');
            if (Array.IndexOf(SessionKeys, key) >= 0 && value.Length > 0 && !value.StartsWith("$'", StringComparison.Ordinal))
            {
                result[key] = value;
            }
        }
        return result;
    }

    internal static Dictionary<string, string> ReadSessionEnvironment()
    {
        try
        {
            var info = new ProcessStartInfo("systemctl")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("--user");
            info.ArgumentList.Add("show-environment");
            DaemonEnvironment.Scrub(info.Environment);
            using var process = NonInteractiveProcess.Start(info);
            if (process is null) { return []; }
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                process.Kill();
                return [];
            }
            return process.ExitCode == 0 ? ParseSessionEnvironment(output.GetAwaiter().GetResult()) : [];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return [];
        }
    }
}
