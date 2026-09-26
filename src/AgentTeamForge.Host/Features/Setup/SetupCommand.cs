using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Runtime.Versioning;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Host.Hosting;

namespace AgentTeamForge.Host.Features.Setup;

/// <summary>Local setup and daemon process controls.</summary>
public static class SetupCommand
{
    const string SettingsFile = "launch-mode.json";
    public const int DefaultWebPort = 8765;

    public static int Run(IReadOnlyDictionary<string, string> options, Func<string, IReadOnlyList<string>, (int ExitCode, string Output)>? commandRunner = null,
        string? executablePath = null, string? claudeSettingsPath = null, string? homePath = null, string? extensionPath = null)
    {
        var check = options.ContainsKey("check");
        var apply = options.ContainsKey("apply");
        var autostart = options.GetValueOrDefault("autostart");
        if (autostart == "on")
        {
            autostart = "true";
        }
        if (autostart is not null and not ("true" or "off"))
        {
            Console.Error.WriteLine("error: --autostart must be on or off");
            return 64;
        }
        if (check && apply)
        {
            Console.Error.WriteLine("error: --check and --apply are mutually exclusive");
            return 64;
        }
        if (options.TryGetValue("web-port", out var webPortText)
            && (!int.TryParse(webPortText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort) || parsedPort is < 1 or > 65535))
        {
            Console.Error.WriteLine("error: --web-port must be between 1 and 65535");
            return 64;
        }
        var dir = ResolveStateDir(options);
        var mode = options.GetValueOrDefault("mode");
        if (mode is null && Directory.Exists(dir))
        {
            mode = ConfiguredMode(StateDirectory.Open(dir));
        }
        if (mode is not ("headless" or "herdr" or "terminal" or "wt") || (!check && !options.ContainsKey("mode") && !(autostart is not null && apply)))
        {
            if (!check && !(autostart == "off" && apply && !options.ContainsKey("mode") && mode is null))
            {
                Console.Error.WriteLine("usage: atf setup --mode headless|herdr|terminal|wt [--web-port PORT] [--autostart[=off]] [--state-dir DIR] [--apply|--check]");
                return 64;
            }
        }
        else if (!ModeAvailable(mode))
        {
            Console.Error.WriteLine($"error: launch mode {mode} is unavailable on this platform");
            return 64;
        }

        var home = homePath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var binary = ClientSetup.StableBinary(executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Executable path unavailable"), home);
        commandRunner ??= RunCommand;
        var settingsPath = claudeSettingsPath ?? Path.Combine(home, ".claude", "settings.json");
        if (check)
        {
            Console.Out.WriteLine($"Login autostart: {(LoginAutostart.IsInstalled(home) ? "on" : "off")}");
            return ClientSetup.Reconcile(binary, dir, home, settingsPath, extensionPath, commandRunner, apply: false) ? 0 : 1;
        }

        if (autostart is not null && apply && !options.ContainsKey("mode"))
        {
            return LoginAutostart.Apply(home, binary, dir, enable: autostart == "true", commandRunner);
        }

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
        var settings = mode == "terminal" ? SelectMacTerminal(Environment.GetEnvironmentVariable("KITTY_LISTEN_ON"), commandRunner)
            : new LaunchModeSettings(mode!);
        var webPort = options.TryGetValue("web-port", out webPortText)
            ? int.Parse(webPortText, CultureInfo.InvariantCulture) : ConfiguredWebPort(state);
        WriteMode(state, settings with { WebPort = webPort });

        if (apply)
        {
            if (!ClientSetup.Reconcile(binary, state.Path, home, settingsPath, extensionPath, commandRunner, apply: true))
            {
                return 1;
            }
            if (autostart is not null && LoginAutostart.Apply(home, binary, state.Path, autostart == "true", commandRunner) != 0)
            {
                return 1;
            }
        }
        else
        {
            foreach (var (tool, args) in Registrations(binary, state.Path))
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
            Console.Out.WriteLine($"Launch mode: {mode}{(mode == "terminal" ? " (" + settings.TerminalProvider + ")" : "")}. Run: {FormatCommand(binary, ["start", "--state-dir", state.Path])}");
        }
        return 0;
    }

    public static async Task<int> StartAsync(IReadOnlyDictionary<string, string> options, string? executablePath = null, bool quiet = false)
    {
        var state = StateDirectory.Open(ResolveStateDir(options));
        var profile = SpikeProfileFile.Load(state);
        var mode = ConfiguredMode(state) ?? (profile.TestProfile ? "headless" : ReadMode(state));
        if (!ModeAvailable(mode))
        {
            Console.Error.WriteLine($"error: launch mode {mode} is unavailable on this platform");
            return 64;
        }
        // Concurrent starters (several MCP bridges) queue here so only one probes and
        // launches; a probe must never overlap a starting daemon's own lock attempt.
        using var gate = await AcquireStartGateAsync(state);
        if (gate is null)
        {
            Console.Error.WriteLine($"error: another daemon start did not finish; see {state.Path}/daemon.log");
            return 1;
        }
        using (var probe = DaemonLock.TryAcquire(state.LockFile))
        {
            if (probe is null)
            {
                return await WaitForReadyAsync(state, quiet);
            }
        }

        var binary = Path.GetFullPath(executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Executable path unavailable"));
        try
        {
            return OperatingSystem.IsWindows()
                ? await StartWindowsAsync(state, binary, quiet)
                : await StartPosixAsync(state, binary, quiet);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"error: daemon could not start: {ex.Message}");
            return 1;
        }
    }

    static async Task<int> StartPosixAsync(StateDirectory state, string binary, bool quiet)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsLinux()
            && LoginAutostart.UseSystemdUserUnit(home, ClientSetup.StableBinary(binary, home), state.Path))
        {
            var service = new ProcessStartInfo("systemctl")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            service.ArgumentList.Add("--user");
            service.ArgumentList.Add("start");
            service.ArgumentList.Add("agentteamforge.service");
            DaemonEnvironment.Scrub(service.Environment);
            using var starter = Process.Start(service) ?? throw new InvalidOperationException("systemctl launch failed");
            starter.StandardInput.Close();
            var output = starter.StandardOutput.ReadToEndAsync();
            var error = starter.StandardError.ReadToEndAsync();
            await starter.WaitForExitAsync();
            if (starter.ExitCode != 0)
            {
                Console.Error.WriteLine($"error: systemd user daemon start failed: {(await error).Trim()} {(await output).Trim()}".Trim());
                return 1;
            }
            await Task.WhenAll(output, error);
            return await WaitForReadyAsync(state, quiet);
        }
        // Linux: setsid separates the daemon from the invoking shell; the shell only
        // redirects its streams and then execs the real atf process. Darwin has no
        // setsid(1): sh backgrounds the daemon and exits, so the launcher PID is not
        // the daemon's and readiness is judged by the lock owner and endpoint alone.
        var info = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/bin/sh" : "setsid") { UseShellExecute = false };
        if (!OperatingSystem.IsMacOS())
        {
            info.ArgumentList.Add("sh");
        }
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(OperatingSystem.IsMacOS()
            ? "umask 077; \"$@\" </dev/null >>\"$ATF_DAEMON_LOG\" 2>&1 &"
            : "umask 077; exec \"$@\" </dev/null >>\"$ATF_DAEMON_LOG\" 2>&1");
        info.ArgumentList.Add("sh");
        info.ArgumentList.Add(binary);
        info.ArgumentList.Add("daemon");
        info.ArgumentList.Add("--state-dir");
        info.ArgumentList.Add(state.Path);
        DaemonEnvironment.Scrub(info.Environment);
        info.Environment["ATF_DAEMON_LOG"] = Path.Combine(state.Path, "daemon.log");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Daemon launch failed");
        return await WaitForReadyAsync(state, quiet, OperatingSystem.IsMacOS() ? null : process);
    }

    public static int Stop(IReadOnlyDictionary<string, string> options)
    {
        var state = StateDirectory.Open(ResolveStateDir(options));
        if (OperatingSystem.IsWindows())
        {
            return StopWindows(state);
        }

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

        if (OperatingSystem.IsMacOS())
        {
            var token = DarwinProcess.CreationToken(pid.Value);
            if (token is null)
            {
                return StoppedDuringCheck(state);
            }

            if (!IsOurDaemon(pid.Value, state.Path))
            {
                Console.Error.WriteLine("error: lock PID is not this state's atf daemon; no signal sent");
                return 1;
            }
            if (!DarwinProcess.SignalIfSame(pid.Value, token.Value, Native.SigTerm))
            {
                return StoppedDuringCheck(state);
            }

            for (var i = 0; i < 50; i++)
            {
                if (LockIsFree(state)) { Console.Out.WriteLine($"Stopped daemon {pid.Value}."); return 0; }
                Thread.Sleep(100);
            }
            Console.Error.WriteLine($"error: daemon {pid.Value} did not stop within 5 seconds");
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

    [SupportedOSPlatform("windows")]
    static async Task<int> StartWindowsAsync(StateDirectory state, string binary, bool quiet)
    {
        using var process = WindowsDaemonLauncher.Start(binary, state.Path);
        return await WaitForReadyAsync(state, quiet, process);
    }

    static async Task<int> WaitForReadyAsync(StateDirectory state, bool quiet, Process? launched = null)
    {
        for (var i = 0; i < 200; i++)
        {
            var pid = DaemonLock.ReadOwnerPid(state.LockFile);
            if (pid is > 0 && (launched is null || pid != launched.Id || OperatingSystem.IsWindows() || ReadyLogged(state, pid.Value))
                && await EndpointReadyAsync(state))
            {
                return PrintRunningPid(state, quiet);
            }
            if (launched?.HasExited == true)
            {
                using var other = DaemonLock.TryAcquire(state.LockFile);
                if (other is not null)
                {
                    Console.Error.WriteLine($"error: daemon exited ({launched.ExitCode}); see {state.Path}/daemon.log");
                    return 1;
                }
            }
            await Task.Delay(50);
        }
        Console.Error.WriteLine($"error: daemon did not become ready; see {state.Path}/daemon.log");
        return 1;
    }

    static async Task<DaemonLock?> AcquireStartGateAsync(StateDirectory state)
    {
        for (var i = 0; i < 300; i++)
        {
            if (DaemonLock.TryAcquire(Path.Combine(state.Path, "start.lock")) is { } gate)
            {
                return gate;
            }
            await Task.Delay(50);
        }
        return null;
    }

    static bool ReadyLogged(StateDirectory state, int pid)
    {
        try
        {
            return File.Exists(Path.Combine(state.Path, "daemon.log")) && File.ReadLines(Path.Combine(state.Path, "daemon.log"))
                .Any(line => line == $"[atf-daemon] ready pid={pid}");
        }
        catch (IOException) { return false; }
    }

    static async Task<bool> EndpointReadyAsync(StateDirectory state)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var pipe = new NamedPipeClientStream(".", state.Socket, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(50);
            }
            else
            {
                using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                using var timeout = new CancellationTokenSource(50);
                await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(state.Socket), timeout.Token);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException or TimeoutException) { return false; }
    }

    static int StopWindows(StateDirectory state)
    {
        if (LockIsFree(state))
        {
            Console.Out.WriteLine("Daemon is not running.");
            return 0;
        }
        var pid = DaemonLock.ReadOwnerPid(state.LockFile);
        if (pid is null || !IsOurWindowsDaemon(pid.Value, state.Path))
        {
            Console.Error.WriteLine("error: lock PID is not this state's atf daemon; no signal sent");
            return 1;
        }
        try
        {
            using var process = Process.GetProcessById(pid.Value);
            // Settle owned agents first so their PowerShell wrappers close their tabs.
            _ = WtInteractiveBackend.RecoverOwned(state.Path);
            process.Kill();
            process.WaitForExit(5000);
            Console.Out.WriteLine($"Stopped daemon {pid.Value}.");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return StoppedDuringCheck(state);
        }
    }

    static bool IsOurWindowsDaemon(int pid, string statePath)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!Path.GetFullPath(process.MainModule?.FileName ?? "").Equals(Path.GetFullPath(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add($"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine");
            using var query = Process.Start(info);
            if (query is null)
            {
                return false;
            }

            var commandLine = query.StandardOutput.ReadToEnd();
            query.WaitForExit(5000);
            var args = query.ExitCode == 0 ? WindowsCommandLine.Split(commandLine.Trim()) : null;
            return args is { Length: >= 4 } && args[1] == "daemon" && args[2] == "--state-dir"
                && Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[3])).Equals(
                    Path.TrimEndingDirectorySeparator(statePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    static bool LockIsFree(StateDirectory state)
    {
        using var probe = DaemonLock.TryAcquire(state.LockFile);
        return probe is not null;
    }

    static bool IsOurDaemon(int pid, string statePath)
    {
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var args = DarwinProcess.Arguments(pid)?.Args;
                return args is { Length: >= 4 } && Path.GetFileName(args[0]) == "atf"
                    && args[1] == "daemon" && args[2] == "--state-dir"
                    && Path.IsPathFullyQualified(args[3])
                    && Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[3])) == Path.TrimEndingDirectorySeparator(statePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }
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
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentTeamForge")
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } xdg ? xdg :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"), "agentteamforge"));

    static int PrintRunningPid(StateDirectory state, bool quiet = false)
    {
        var pid = DaemonLock.ReadOwnerPid(state.LockFile);
        if (pid is null)
        {
            Console.Error.WriteLine("error: daemon running but PID unavailable");
            return 1;
        }

        if (!quiet)
        {
            Console.Out.WriteLine(pid.Value);
        }
        return 0;
    }

    static void WriteMode(StateDirectory state, LaunchModeSettings settings)
    {
        var path = Path.Combine(state.Path, SettingsFile);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = OperatingSystem.IsWindows() ? null : StateDirectory.PrivateFile,
            }))
            {
                JsonSerializer.Serialize(file, settings, SetupCommandJson.Default.LaunchModeSettings);
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

    internal static void EnableClaudeInbound(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        StateDirectory.CreatePrivateDirectory(parent);
        using var existing = File.Exists(path)
            ? JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
            : null;
        if (existing is not null && existing.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Claude settings must be an object");
        }
        if (existing?.RootElement.TryGetProperty("crossSessionInbound", out var inbound) == true
            && inbound.ValueKind == JsonValueKind.String && inbound.GetString() == "accept")
        {
            return;
        }

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = OperatingSystem.IsWindows() ? null : StateDirectory.PrivateFile,
            }))
            {
                using var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
                writer.WriteStartObject();
                if (existing is not null)
                {
                    foreach (var property in existing.RootElement.EnumerateObject())
                    {
                        if (property.Name == "crossSessionInbound")
                        {
                            continue;
                        }
                        property.WriteTo(writer);
                    }
                }
                writer.WriteString("crossSessionInbound", "accept");
                writer.WriteEndObject();
                writer.Flush();
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

    static LaunchModeSettings ReadSettings(StateDirectory state)
    {
        var path = Path.Combine(state.Path, SettingsFile);
        var settings = JsonSerializer.Deserialize(StateDirectory.ReadPrivateFile(path), SetupCommandJson.Default.LaunchModeSettings);
        if (settings?.Mode is not ("headless" or "herdr" or "terminal" or "wt")
            || settings.Mode == "terminal" && (settings.TerminalProvider is not ("terminal" or "kitty")
                || settings.TerminalProvider == "kitty" && (settings.KittyAddress is null || settings.KittyBinary is null))
            || settings.WebPort is not (>= 1 and <= 65535))
        {
            throw new StateDirectoryException("launch_mode_invalid");
        }

        return settings;
    }

    static string ReadMode(StateDirectory state) => ReadSettings(state).Mode;

    internal static LaunchModeSettings SelectMacTerminal(string? address,
        Func<string, IReadOnlyList<string>, (int ExitCode, string Output)> runner, string? kittyBinary = null)
    {
        if (address is { Length: > 0 } && address.StartsWith("unix:", StringComparison.Ordinal)
            && (kittyBinary ?? FindExecutable("kitty")) is { } binary)
        {
            try
            {
                if (runner(binary, ["@", "--to", address, "ls"]).ExitCode == 0)
                {
                    return new("terminal", "kitty", address, binary);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return new("terminal", "terminal");
    }

    static string? FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (directory.Length > 0 && File.Exists(Path.Combine(directory, name)))
            {
                return Path.Combine(directory, name);
            }
        }
        return null;
    }

    static bool ModeAvailable(string mode) => mode switch
    {
        "wt" => OperatingSystem.IsWindows(),
        "terminal" => OperatingSystem.IsMacOS(),
        "herdr" => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
        _ => true,
    };

    internal static string? ConfiguredMode(StateDirectory state) =>
        File.Exists(Path.Combine(state.Path, SettingsFile)) ? ReadMode(state) : null;

    internal static LaunchModeSettings? ConfiguredTerminal(StateDirectory state) =>
        File.Exists(Path.Combine(state.Path, SettingsFile)) && ReadSettings(state) is { Mode: "terminal" } settings ? settings : null;

    public static int ConfiguredWebPort(StateDirectory state) =>
        File.Exists(Path.Combine(state.Path, SettingsFile)) ? ReadSettings(state).WebPort : DefaultWebPort;

    internal static (int ExitCode, string Output) RunCommand(string tool, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : tool)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (OperatingSystem.IsWindows())
        {
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-Command");
            // Keep the not-installed (127) and native exit-code contract of the direct launch.
            info.ArgumentList.Add($"if (-not (Get-Command -Name {QuotePowerShell(tool)} -CommandType Application,ExternalScript -ErrorAction SilentlyContinue)) {{ exit 127 }}; & "
                + string.Join(' ', new[] { tool }.Concat(args).Select(QuotePowerShell)) + "; exit $LASTEXITCODE");
        }
        else
        {
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }
        }

        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException($"Cannot start {tool}");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var output = stdout.GetAwaiter().GetResult();
            _ = stderr.GetAwaiter().GetResult();
            return (process.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (127, "");
        }
    }

    static string FormatCommand(string tool, IReadOnlyList<string> args) => OperatingSystem.IsWindows()
        ? "powershell -NoProfile -Command \"& " + string.Join(' ', new[] { tool }.Concat(args).Select(QuotePowerShell)) + "\""
        : string.Join(' ', new[] { tool }.Concat(args).Select(Quote));

    static string QuotePowerShell(string value) => PowerShellText.Quote(value);

    static string Quote(string value) => value.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.')
        ? value : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

public sealed record LaunchModeSettings(string Mode, string? TerminalProvider = null, string? KittyAddress = null, string? KittyBinary = null)
{
    public int WebPort { get; init; } = SetupCommand.DefaultWebPort;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(LaunchModeSettings))]
public sealed partial class SetupCommandJson : JsonSerializerContext;
