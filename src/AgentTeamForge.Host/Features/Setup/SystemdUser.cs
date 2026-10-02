using System.Diagnostics;
using AgentTeamForge.Business.Features.Processes;

namespace AgentTeamForge.Host.Features.Setup;

internal static class SystemdUser
{
    static readonly string[] SessionKeys = ["WAYLAND_DISPLAY", "DISPLAY", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS"];

    // A lazily started daemon must not live in the caller's cgroup: tearing down a
    // terminal or desktop-app scope SIGKILLs everything in it, including the daemon.
    // It runs as a transient user service, so systemd also records how it ended.
    internal static bool ServiceAvailable(Func<string, string?> env, Func<string, string?> findExecutable) =>
        OperatingSystem.IsLinux()
        && findExecutable("systemd-run") is not null
        && env("XDG_RUNTIME_DIR") is { Length: > 0 } runtime
        && Directory.Exists(Path.Combine(runtime, "systemd"))
        && File.Exists(Path.Combine(runtime, "bus"));

    // Hosts such as Codex scrub XDG_RUNTIME_DIR from MCP servers. logind's per-user
    // directory is still there, and systemctl/systemd-run --user need only it to reach
    // the user manager. /run/user is root-owned, so /run/user/<euid> is ours.
    internal static void EnsureRuntimeDir(IDictionary<string, string?> environment, string? userRuntimeDir = null)
    {
        if (!OperatingSystem.IsLinux()
            || (environment.TryGetValue("XDG_RUNTIME_DIR", out var current) && !string.IsNullOrEmpty(current)))
        {
            return;
        }
        var dir = userRuntimeDir ?? "/run/user/" + AgentTeamForge.Host.Hosting.Native.geteuid().ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Directory.Exists(Path.Combine(dir, "systemd")) && File.Exists(Path.Combine(dir, "bus")))
        {
            environment["XDG_RUNTIME_DIR"] = dir;
        }
    }

    internal const string UnitFile = "daemon.unit";
    internal const string ExitFile = "daemon.exit";
    internal const string EnvFile = "daemon.env";

    internal static string NewUnitName() => "agentteamforge-daemon-" + Guid.NewGuid().ToString("N")[..8];

    // Property values go through % specifier expansion; Exec lines additionally through $VAR expansion.
    static string Specifiers(string value) => value.Replace("%", "%%", StringComparison.Ordinal);

    // One argument of an Exec line: double-quoted with C-style escapes, plus % and $ doubled.
    internal static string ExecArgument(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal)
            .Replace("$", "$$", StringComparison.Ordinal) + "\"";

    // KillMode=process: herdr servers and agent panes started by the daemon must outlive it.
    // The environment is NOT passed on the command line (it is world-readable in /proc and the
    // journal); a service inherits nothing from the caller, so it comes from a 0600 EnvironmentFile.
    // --collect unloads the unit at once even after a failure, so ExecStopPost records the result.
    // The working directory is the starter's, as with the scope: a relative CODEX_HOME resolves against it.
    internal static IReadOnlyList<string> ServicePrefix(string stateDir, string unit, string logPath, string? workingDirectory = null)
    {
        List<string> args =
        [
            "--user", "--quiet", "--collect", "--unit=" + unit,
            "--description=AgentTeamForge daemon (" + stateDir + ")",
            "-p", "Type=exec", "-p", "Restart=no", "-p", "KillMode=process", "-p", "UMask=0077",
            "-p", "EnvironmentFile=-" + Specifiers(Path.Combine(stateDir, EnvFile)),
            "-p", "StandardOutput=append:" + Specifiers(logPath),
            "-p", "StandardError=append:" + Specifiers(logPath),
        ];
        if (workingDirectory is { Length: > 0 })
        {
            args.AddRange(["-p", "WorkingDirectory=-" + Specifiers(workingDirectory)]);
        }
        // The path is a separate argument ($1), never part of the shell program text.
        args.AddRange(["-p", "ExecStopPost=/bin/sh -c 'echo \"result=$$SERVICE_RESULT code=$$EXIT_CODE status=$$EXIT_STATUS\" > \"$$1\"' sh "
            + ExecArgument(Path.Combine(stateDir, ExitFile)), "--"]);
        return args;
    }

    // systemd EnvironmentFile: KEY="value"; inside the quotes only \" and \\ (and \` \$) are escapes,
    // and raw newlines and carriage returns are kept as they are.
    internal static string EnvironmentFileContent(IEnumerable<KeyValuePair<string, string?>> environment)
    {
        var text = new System.Text.StringBuilder();
        foreach (var (key, value) in environment)
        {
            if (value is null || key.Length == 0 || !(char.IsAsciiLetter(key[0]) || key[0] == '_') || !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                continue;
            }
            text.Append(key).Append("=\"")
                .Append(value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal))
                .Append("\"\n");
        }
        return text.ToString();
    }

    internal static void WriteEnvironmentFile(string path, IEnumerable<KeyValuePair<string, string?>> environment)
    {
        File.Delete(path);
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        using var writer = new StreamWriter(stream);
        writer.Write(EnvironmentFileContent(environment));
    }

    /// <summary>What systemd recorded for the previous daemon's end: the ExecStopPost file, else the unit if still loaded.</summary>
    internal static string? LastExitResult(string stateDir, Func<string, string?>? showUnit = null)
    {
        try
        {
            var exit = Path.Combine(stateDir, ExitFile);
            if (File.Exists(exit) && File.ReadAllText(exit).Trim() is { Length: > 0 and < 200 } text && text.All(c => c is >= ' ' and < '\u007f'))
            {
                return text;
            }
            var unit = Path.Combine(stateDir, UnitFile);
            if (File.Exists(unit) && File.ReadAllText(unit).Trim() is { Length: > 0 and < 100 } name && name.StartsWith("agentteamforge-daemon-", StringComparison.Ordinal))
            {
                return (showUnit ?? ShowUnit)(name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    static string? ShowUnit(string unit)
    {
        try
        {
            var info = new ProcessStartInfo("systemctl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--user", "show", unit + ".service", "-p", "LoadState,Result,ExecMainCode,ExecMainStatus" }) { info.ArgumentList.Add(arg); }
            DaemonEnvironment.Scrub(info.Environment);
            EnsureRuntimeDir(info.Environment);
            using var process = NonInteractiveProcess.Start(info);
            if (process is null) { return null; }
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(5))) { process.Kill(); return null; }
            var text = output.GetAwaiter().GetResult();
            return process.ExitCode == 0 && !text.Contains("LoadState=not-found", StringComparison.Ordinal)
                ? string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith("LoadState", StringComparison.Ordinal)))
                : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

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
            EnsureRuntimeDir(info.Environment);
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
