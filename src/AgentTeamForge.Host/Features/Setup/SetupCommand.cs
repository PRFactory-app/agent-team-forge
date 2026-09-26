using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Host.Hosting;

namespace AgentTeamForge.Host.Features.Setup;

/// <summary>Local Linux setup and daemon process controls.</summary>
public static class SetupCommand
{
    const string SettingsFile = "launch-mode.json";

    public static int Run(IReadOnlyDictionary<string, string> options, Func<string, IReadOnlyList<string>, int>? commandRunner = null,
        string? executablePath = null)
    {
        if (!options.TryGetValue("mode", out var mode) || mode is not ("headless" or "herdr"))
        {
            Console.Error.WriteLine("usage: atf setup --mode headless|herdr [--state-dir DIR] [--apply]");
            return 64;
        }

        var dir = ResolveStateDir(options);
        if (!File.Exists(Path.Combine(dir, "profile.json")))
        {
            var result = InitCommand.Run(dir, testProfile: false, queueLimit: null, maxRuntimeSeconds: null);
            if (result != 0)
            {
                return result;
            }
        }

        var state = StateDirectory.Open(dir);
        if (!SpikeProfileFile.Load(state).RealAgents)
        {
            Console.Error.WriteLine("error: setup requires an agents profile");
            return 78;
        }
        WriteMode(state, mode);

        var binary = Path.GetFullPath(executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Executable path unavailable"));
        commandRunner ??= RunCommand;
        foreach (var (tool, args) in Registrations(binary, state.Path))
        {
            if (options.ContainsKey("apply"))
            {
                if (commandRunner(tool, ["mcp", "get", "agentteamforge"]) == 0)
                {
                    Console.Out.WriteLine($"{tool}: already registered");
                    continue;
                }

                if (commandRunner(tool, args) != 0)
                {
                    Console.Error.WriteLine($"error: {tool} MCP registration failed");
                    return 1;
                }

                Console.Out.WriteLine($"{tool}: registered");
            }
            else
            {
                Console.Out.WriteLine(FormatCommand(tool, args));
            }
        }

        if (mode == "headless")
        {
            Console.Out.WriteLine($"Launch mode: headless. Run: {FormatCommand(binary, ["start", "--state-dir", state.Path])}");
        }
        else
        {
            Console.Out.WriteLine($"Launch mode: herdr. Run: {FormatCommand(binary, ["start", "--state-dir", state.Path])}");
        }
        return 0;
    }

    public static async Task<int> StartAsync(IReadOnlyDictionary<string, string> options, string? executablePath = null)
    {
        var state = StateDirectory.Open(ResolveStateDir(options));
        _ = SpikeProfileFile.Load(state);
        _ = ReadMode(state);
        using (var probe = DaemonLock.TryAcquire(state.LockFile))
        {
            if (probe is null)
            {
                return PrintRunningPid(state);
            }
        }

        var binary = Path.GetFullPath(executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Executable path unavailable"));
        // setsid separates the daemon from the invoking shell; the shell only
        // redirects its streams and then execs the real atf process.
        var info = new ProcessStartInfo("setsid") { UseShellExecute = false };
        info.ArgumentList.Add("sh");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("umask 077; exec \"$@\" </dev/null >>\"$ATF_DAEMON_LOG\" 2>&1");
        info.ArgumentList.Add("sh");
        info.ArgumentList.Add(binary);
        info.ArgumentList.Add("daemon");
        info.ArgumentList.Add("--state-dir");
        info.ArgumentList.Add(state.Path);
        info.Environment["ATF_DAEMON_LOG"] = Path.Combine(state.Path, "daemon.log");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Daemon launch failed");
        // The daemon holds the lock for its lifetime and writes its PID under that lock.
        for (var i = 0; i < 100; i++)
        {
            if (process.HasExited)
            {
                using var other = DaemonLock.TryAcquire(state.LockFile);
                if (other is null)
                {
                    return PrintRunningPid(state);
                }

                Console.Error.WriteLine($"error: daemon exited ({process.ExitCode}); see {state.Path}/daemon.log");
                return 1;
            }

            // Do not probe the lock here: holding it even briefly can make the
            // starting daemon lose the race and exit. setsid/sh exec keep the PID.
            if (DaemonLock.ReadOwnerPid(state.LockFile) == process.Id && File.Exists(state.Socket))
            {
                return PrintRunningPid(state);
            }

            await Task.Delay(50);
        }

        Console.Error.WriteLine("error: daemon did not become ready");
        return 1;
    }

    public static int Stop(IReadOnlyDictionary<string, string> options)
    {
        var state = StateDirectory.Open(ResolveStateDir(options));
        if (LockIsFree(state))
        {
            Console.Out.WriteLine("Daemon is not running.");
            return 0;
        }

        var pid = DaemonLock.ReadOwnerPid(state.LockFile);
        if (pid is null)
        {
            Console.Error.WriteLine("error: daemon lock is held but its PID is unavailable");
            return 1;
        }

        using var process = Pidfd.Open(pid.Value);
        if (process is null)
        {
            return StoppedDuringCheck(state);
        }

        if (!IsOurDaemon(pid.Value, state.Path))
        {
            if (LockIsFree(state))
            {
                Console.Out.WriteLine("Daemon is not running.");
                return 0;
            }

            Console.Error.WriteLine("error: lock PID is not this state's atf daemon; no signal sent");
            return 1;
        }

        if (!Pidfd.Signal(process, Native.SigTerm))
        {
            return StoppedDuringCheck(state);
        }

        for (var i = 0; i < 50; i++)
        {
            if (LockIsFree(state))
            {
                Console.Out.WriteLine($"Stopped daemon {pid.Value}.");
                return 0;
            }

            Thread.Sleep(100);
        }

        Console.Error.WriteLine($"error: daemon {pid.Value} did not stop within 5 seconds");
        return 1;
    }

    static int StoppedDuringCheck(StateDirectory state)
    {
        if (LockIsFree(state))
        {
            Console.Out.WriteLine("Daemon is not running.");
            return 0;
        }

        Console.Error.WriteLine("error: daemon process could not be signaled; no signal sent");
        return 1;
    }

    static bool LockIsFree(StateDirectory state)
    {
        using var probe = DaemonLock.TryAcquire(state.LockFile);
        return probe is not null;
    }

    static bool IsOurDaemon(int pid, string statePath)
    {
        try
        {
            var proc = $"/proc/{pid}";
            var exe = new FileInfo(Path.Combine(proc, "exe")).LinkTarget;
            const string deleted = " (deleted)";
            if (exe is null || Path.GetFileName(exe.EndsWith(deleted, StringComparison.Ordinal) ? exe[..^deleted.Length] : exe) != "atf")
            {
                return false;
            }

            var uid = File.ReadLines(Path.Combine(proc, "status"))
                .FirstOrDefault(line => line.StartsWith("Uid:", StringComparison.Ordinal))?
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (uid is not { Length: >= 2 } || !uint.TryParse(uid[1], out var owner) || owner != Native.geteuid())
            {
                return false;
            }

            var args = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(proc, "cmdline")))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (args.Length < 4 || Path.GetFileName(args[0]) != "atf"
                || args[1] != "daemon" || args[2] != "--state-dir")
            {
                return false;
            }

            var cwd = Path.IsPathFullyQualified(args[3]) ? null : new DirectoryInfo(Path.Combine(proc, "cwd")).LinkTarget;
            if (!Path.IsPathFullyQualified(args[3]) && cwd is null)
            {
                return false;
            }

            var daemonState = cwd is null ? args[3] : Path.Combine(cwd, args[3]);
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(daemonState))
                == Path.TrimEndingDirectorySeparator(statePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<(string Tool, IReadOnlyList<string> Args)> Registrations(string binary, string stateDir) =>
    [
        ("claude", ["mcp", "add", "--scope", "user", "agentteamforge", "--", binary, "mcp", "--state-dir", stateDir]),
        ("codex", ["mcp", "add", "agentteamforge", "--", binary, "mcp", "--state-dir", stateDir]),
    ];

    internal static string ResolveStateDir(IReadOnlyDictionary<string, string> options) => Path.GetFullPath(
        options.TryGetValue("state-dir", out var specified) ? specified :
        Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } xdg ? xdg :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"), "agentteamforge"));

    static int PrintRunningPid(StateDirectory state)
    {
        var pid = DaemonLock.ReadOwnerPid(state.LockFile);
        if (pid is null)
        {
            Console.Error.WriteLine("error: daemon running but PID unavailable");
            return 1;
        }

        Console.Out.WriteLine(pid.Value);
        return 0;
    }

    static void WriteMode(StateDirectory state, string mode)
    {
        var path = Path.Combine(state.Path, SettingsFile);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = StateDirectory.PrivateFile,
            }))
            {
                JsonSerializer.Serialize(file, new LaunchModeSettings(mode), SetupCommandJson.Default.LaunchModeSettings);
                file.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    static string ReadMode(StateDirectory state)
    {
        var path = Path.Combine(state.Path, SettingsFile);
        var settings = JsonSerializer.Deserialize(StateDirectory.ReadPrivateFile(path), SetupCommandJson.Default.LaunchModeSettings);
        if (settings?.Mode is not ("headless" or "herdr"))
        {
            throw new StateDirectoryException("launch_mode_invalid");
        }

        return settings.Mode;
    }

    internal static string? ConfiguredMode(StateDirectory state) =>
        File.Exists(Path.Combine(state.Path, SettingsFile)) ? ReadMode(state) : null;

    static int RunCommand(string tool, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException($"Cannot start {tool}");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            _ = stdout.GetAwaiter().GetResult();
            _ = stderr.GetAwaiter().GetResult();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return 127;
        }
    }

    static string FormatCommand(string tool, IReadOnlyList<string> args) =>
        string.Join(' ', new[] { tool }.Concat(args).Select(Quote));

    static string Quote(string value) => value.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.')
        ? value : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

public sealed record LaunchModeSettings(string Mode);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(LaunchModeSettings))]
public sealed partial class SetupCommandJson : JsonSerializerContext;
