using AgentTeamForge.DAL.Files;
using System.Diagnostics;
using AgentTeamForge.Business.Features.Processes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Runtime.Versioning;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Setup;

/// <summary>Local setup and daemon process controls.</summary>
public static class SetupCommand
{
    const string SettingsFile = "launch-mode.json";
    public const int DefaultWebPort = 8765;

    public static int Run(IReadOnlyDictionary<string, string> options, Func<string, IReadOnlyList<string>, (int ExitCode, string Output)>? commandRunner = null,
        string? executablePath = null, string? claudeSettingsPath = null, string? homePath = null, string? extensionPath = null,
        TextReader? input = null, bool? interactive = null, Func<string, string?>? clientEnvironment = null)
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
        if (LegacyWindowsStateDir(options, dir) is { } legacy)
        {
            // Never silently orphan jobs, keys and registrations left in the old default.
            Console.Error.WriteLine($"{(check ? "warning" : "error")}: existing state is in the old default {legacy}; the default is now {dir}.");
            Console.Error.WriteLine($"Stop its daemon (atf stop --state-dir \"{legacy}\"), move the directory to {dir}, then rerun setup; or pass --state-dir \"{legacy}\" (not usable with --mode wt).");
            if (!check)
            {
                return 64;
            }
            // The old state must be moved before registrations can be checked against
            // the new default. Reporting missing registrations here is misleading.
            return 1;
        }
        var mode = options.GetValueOrDefault("mode");
        var configuredMode = Directory.Exists(dir) ? ConfiguredMode(StateDirectory.Open(dir)) : null;
        mode ??= configuredMode;
        commandRunner ??= RunCommand;
        if (mode is null && !check && autostart is null)
        {
            if (!(interactive ?? !Console.IsInputRedirected))
            {
                Console.Error.WriteLine("error: fresh non-interactive setup needs --mode headless or --mode herdr (or run in a terminal to choose)");
                return 64;
            }
            mode = ChooseMode(input ?? Console.In, commandRunner);
            if (mode is null)
            {
                return 64;
            }
        }
        if (mode is not ("headless" or "herdr" or "terminal" or "wt")
            && !(check && mode is null) && !(autostart == "off" && !check && configuredMode is null))
        {
            Console.Error.WriteLine("usage: atf setup [--mode headless|herdr|terminal|wt] [--web-port PORT] [--autostart[=off]] [--state-dir DIR] [--check|--apply] [--force]");
            return 64;
        }
        if (mode is not null && !ModeAvailable(mode))
        {
            Console.Error.WriteLine($"error: launch mode {mode} is unavailable on this platform");
            return 64;
        }
        if (mode == "herdr" && !check && HerdrProblem(commandRunner) is { } herdrProblem)
        {
            Console.Error.WriteLine($"error: herdr is unavailable: {herdrProblem}; choose --mode headless explicitly if desired");
            return 64;
        }

        var home = homePath ?? ClientHome();
        var executable = executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Executable path unavailable");
        var binary = ClientSetup.StableBinary(executable, home);
        var settingsPath = claudeSettingsPath ?? Path.Combine(home, ".claude", "settings.json");
        if (check)
        {
            Console.Out.WriteLine($"Login autostart: {(LoginAutostart.IsInstalled(home) ? "on" : "off")}");
            var configured = new[] { BackendCatalog.Claude, BackendCatalog.Codex, BackendCatalog.Pi,
                BackendCatalog.Cursor, BackendCatalog.Droid };
            var installed = BackendAvailability.ReadInstalled(configured);
            var available = BackendAvailability.Read(configured, mode);
            var signIn = BackendAvailability.ReadSignIn(installed);
            foreach (var backend in configured)
            {
                Console.Out.WriteLine($"Backend {backend}: {(installed[backend] ? "installed" : "not installed")}, " +
                    $"{(available[backend] ? "available" : "unavailable")}, sign-in {signIn[backend]}");
            }
            return ClientSetup.Reconcile(binary, dir, home, settingsPath, extensionPath, commandRunner, apply: false) ? 0 : 1;
        }

        if (!(autostart == "off" && !options.ContainsKey("mode"))
            && ClientConfigProblem(dir, home, options.ContainsKey("force"),
                clientEnvironment ?? (homePath is null ? Environment.GetEnvironmentVariable : _ => null)) is { } configProblem)
        {
            Console.Error.WriteLine($"error: {configProblem}");
            return 64;
        }

        var unsafeBinary = UnsafeRegistrationPath(executable, home);
        var unsafeState = UnsafeRegistrationPath(dir, home);
        // Temporary or worktree state belongs only in an isolated HOME; --force never overrides this.
        if (!(autostart == "off" && !options.ContainsKey("mode")) && unsafeState is not null && UnsafeRegistrationPath(home, home) is null)
        {
            Console.Error.WriteLine($"error: setup would register {unsafeState} in the client configs of HOME={home}; --force cannot override this.");
            Console.Error.WriteLine("For isolated tests, set HOME (and CODEX_HOME, CLAUDE_CONFIG_DIR) to a temporary directory as well.");
            return 64;
        }
        // Disabling autostart removes registrations only, so it never needs the guard.
        if (!(autostart == "off" && !options.ContainsKey("mode")) && !options.ContainsKey("force") && (unsafeBinary is not null || unsafeState is not null))
        {
            Console.Error.WriteLine($"error: setup would write global client registrations using an unsafe {(unsafeBinary is not null ? "binary" : "state directory")} path: {unsafeBinary ?? unsafeState}");
            Console.Error.WriteLine("Use --force only if this is intentional. For testing, use atf start --state-dir DIR or atf mcp --state-dir DIR.");
            return 64;
        }

        if (autostart is not null && !options.ContainsKey("mode"))
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
        if (!ProfileFile.Load(state).RealAgents)
        {
            Console.Error.WriteLine("error: setup requires an agents profile");
            return 78;
        }
        var settings = mode == "terminal" ? SelectMacTerminal(Environment.GetEnvironmentVariable("KITTY_LISTEN_ON"), commandRunner)
            : new LaunchModeSettings(mode!);
        var webPort = options.TryGetValue("web-port", out webPortText)
            ? int.Parse(webPortText, CultureInfo.InvariantCulture) : ConfiguredWebPort(state);
        WriteMode(state, settings with { WebPort = webPort });

        // A failed client `mcp add` is non-fatal (launch config is written; --check is the health gate). Other failures keep exit 1.
        if (!ClientSetup.Reconcile(binary, state.Path, home, settingsPath, extensionPath, commandRunner, apply: true,
            registrationFailureNonFatal: true))
        {
            return 1;
        }
        if (autostart is not null && LoginAutostart.Apply(home, binary, state.Path, autostart == "true", commandRunner) != 0)
        {
            return 1;
        }

        Console.Out.WriteLine($"Launch mode: {mode}{(mode == "terminal" ? " (" + settings.TerminalProvider + ")" : "")}");
        Console.Out.WriteLine($"Binary: {binary}");
        Console.Out.WriteLine($"State: {state.Path}");
        Console.Out.WriteLine("The daemon starts on first use.");
        return 0;
    }

    static string? ChooseMode(TextReader input, Func<string, IReadOnlyList<string>, (int ExitCode, string Output)> run)
    {
        var problem = HerdrProblem(run);
        Console.Out.WriteLine(problem is null
            ? "Recommended launch mode: herdr (interactive agent windows are available)."
            : $"Recommended launch mode: headless (Herdr unavailable: {problem}).");
        Console.Out.Write("Choose launch mode by typing herdr or headless: ");
        var choice = input.ReadLine()?.Trim().ToLowerInvariant();
        if (choice is "headless" or "herdr")
        {
            return choice;
        }
        Console.Error.WriteLine("error: type herdr or headless to confirm a launch mode");
        return null;
    }

    static string? HerdrProblem(Func<string, IReadOnlyList<string>, (int ExitCode, string Output)> run)
    {
        if (!ModeAvailable("herdr"))
        {
            return "unsupported platform";
        }
        if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return "no graphical desktop session (WAYLAND_DISPLAY or DISPLAY)";
        }
        var (exitCode, output) = run("herdr", ["--version"]);
        return exitCode == 0 ? null : exitCode == 127 ? "herdr executable not found" :
            $"herdr --version failed: {ClientSetup.BoundedError(output)}";
    }

    static string? UnsafeRegistrationPath(string path, string home)
    {
        var full = Path.GetFullPath(path);
        var homeFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (Within(full, "/tmp") || Within(full, temp))
        {
            return $"{full} is under a temporary directory";
        }
        if (full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part is ".worktrees" or "artifacts"))
        {
            return $"{full} is inside .worktrees or artifacts";
        }
        // Stop at HOME: a dotfiles repository in HOME must not flag every normal install.
        for (var parent = Path.GetDirectoryName(full); parent is not null && !Within(homeFull, parent); parent = Path.GetDirectoryName(parent))
        {
            if (File.Exists(Path.Combine(parent, ".git")) || Directory.Exists(Path.Combine(parent, ".git")))
            {
                return $"{full} is inside a git worktree";
            }
        }
        return null;
    }

    static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    static bool Within(string path, string root) => path.Equals(Path.TrimEndingDirectorySeparator(root), PathComparison)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison);

    internal static string ClientHome() => OperatingSystem.IsWindows()
        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        : Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home
            ? Path.GetFullPath(home)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    internal static string? ClientConfigProblem(string stateDir, string home, bool force, Func<string, string?> environment)
    {
        var defaultState = Path.GetFullPath(Path.Combine(
            environment("XDG_STATE_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".local", "state"),
            "agentteamforge"));
        var nonDefault = !Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateDir))
            .Equals(Path.TrimEndingDirectorySeparator(defaultState), PathComparison);
        if (nonDefault && !force)
        {
            return $"non-default state directory {stateDir} would change client registrations; use --force only with the intended client HOME";
        }
        foreach (var name in new[] { "CODEX_HOME", "CLAUDE_CONFIG_DIR" })
        {
            if (environment(name) is { Length: > 0 } configured && !Within(Path.GetFullPath(configured), Path.GetFullPath(home)))
            {
                return $"{name}={configured} is outside HOME={home}; refusing client config writes for state directory {stateDir}";
            }
        }
        return null;
    }

    public static async Task<int> StartAsync(IReadOnlyDictionary<string, string> options, string? executablePath = null, bool quiet = false)
    {
        var state = StateDirectory.Open(ResolveStateDir(options));
        var profile = ProfileFile.Load(state);
        var mode = ConfiguredMode(state) ?? (profile.TestProfile ? "headless" : ReadMode(state));
        if (!ModeAvailable(mode))
        {
            Console.Error.WriteLine($"error: launch mode {mode} is unavailable on this platform");
            return 64;
        }
        // Concurrent starters (several MCP bridges) queue here so only one probes and
        // launches; a probe must never overlap a starting daemon's own lock attempt.
        DaemonLock? gate;
        try
        {
            // Check the endpoint before taking a start lock: an existing daemon may
            // belong to another user, whose state directory also denies the lock.
            _ = await EndpointReadyAsync(state);
            gate = await AcquireStartGateAsync(state);
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {IpcProtocol.AccessDenied}: {IpcClient.AccessDeniedDetail}");
            return 1;
        }
        using var acquiredGate = gate;
        if (gate is null)
        {
            Console.Error.WriteLine($"error: another daemon start did not finish; see {state.Path}/daemon.log");
            return 1;
        }
        try
        {
            using var probe = DaemonLock.TryAcquire(state.LockFile);
            if (probe is null)
            {
                return await WaitForReadyAsync(state, quiet);
            }
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {IpcProtocol.AccessDenied}: {IpcClient.AccessDeniedDetail}");
            return 1;
        }

        var binary = Path.GetFullPath(executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Executable path unavailable"));
        try
        {
            return OperatingSystem.IsWindows()
                ? await StartWindowsAsync(state, binary, quiet)
                : await StartPosixAsync(state, binary, quiet, profile.TestProfile);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"error: daemon could not start: {ex.Message}");
            return 1;
        }
    }

    static async Task<int> StartPosixAsync(StateDirectory state, string binary, bool quiet, bool testProfile)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsLinux()
            && LoginAutostart.UseSystemdUserUnit(home, ClientSetup.StableBinary(binary, home), state.Path))
        {
            var service = new ProcessStartInfo("systemctl")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            service.ArgumentList.Add("--user");
            service.ArgumentList.Add("start");
            service.ArgumentList.Add("agentteamforge.service");
            DaemonEnvironment.Scrub(service.Environment);
            using var starter = NonInteractiveProcess.Start(service) ?? throw new InvalidOperationException("systemctl launch failed");
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
        using var input = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.Read);
        using var log = new FileStream(Path.Combine(state.Path, "daemon.log"),
            PrivateFiles.Options(FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
        // A scope execs in place (same PID, fds, env) but leaves the caller's cgroup, so its
        // teardown cannot SIGKILL the daemon. Test profiles never create scopes.
        var scope = !testProfile && SystemdUser.ScopeAvailable(Environment.GetEnvironmentVariable, FindExecutable);
        var info = new ProcessStartInfo(scope ? "systemd-run" : binary)
        {
            UseShellExecute = false,
            StartDetached = true,
            StandardInputHandle = input,
            StandardOutputHandle = log.SafeFileHandle,
            StandardErrorHandle = log.SafeFileHandle,
            InheritedHandles = [],
        };
        if (scope)
        {
            foreach (var arg in SystemdUser.ScopePrefix(state.Path)) { info.ArgumentList.Add(arg); }
            info.ArgumentList.Add(binary);
        }
        info.ArgumentList.Add("daemon");
        info.ArgumentList.Add("--state-dir");
        info.ArgumentList.Add(state.Path);
        DaemonEnvironment.Scrub(info.Environment);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Daemon launch failed");
        return await WaitForReadyAsync(state, quiet, process);
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
            try
            {
                if (pid is > 0 && (launched is null || pid != launched.Id || OperatingSystem.IsWindows() || ReadyLogged(state, pid.Value))
                    && await EndpointReadyAsync(state))
                {
                    return PrintRunningPid(state, quiet);
                }
            }
            catch (UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"error: {IpcProtocol.AccessDenied}: {IpcClient.AccessDeniedDetail}");
                return 1;
            }
            if (launched?.HasExited == true)
            {
                using var other = DaemonLock.TryAcquire(state.LockFile);
                if (other is not null)
                {
                    Console.Error.WriteLine(OperatingSystem.IsWindows()
                        ? $"error: daemon exited; see {state.Path}/daemon.log"
                        : $"error: daemon exited ({launched.ExitCode}); see {state.Path}/daemon.log");
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
            return File.Exists(Path.Combine(state.Path, "daemon.log")) && LiveFiles.ReadLines(Path.Combine(state.Path, "daemon.log"))
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
                using var timeout = new CancellationTokenSource(50);
                using var pipe = await WindowsPipe.ConnectAsync(state.Socket, timeout.Token);
            }
            else
            {
                using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                using var timeout = new CancellationTokenSource(50);
                await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(state.Socket), timeout.Token);
            }
            return true;
        }
        catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AccessDenied)
        {
            throw new UnauthorizedAccessException(IpcClient.AccessDeniedDetail, ex);
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
            using var query = NonInteractiveProcess.Start(info);
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

    internal static string ResolveStateDir(IReadOnlyDictionary<string, string> options) => Path.GetFullPath(
        options.TryGetValue("state-dir", out var specified) ? specified :
        // One home-based default on every OS. Not %LOCALAPPDATA% on Windows: Windows Terminal is a
        // packaged (MSIX) app whose tabs see a virtualized AppData\Local where directories created by
        // unpackaged processes are invisible, so `wt ... powershell -File <state>\wt\*.ps1` fails.
        Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } xdg ? xdg :
            Path.Combine(ClientHome(), ".local", "state"), "agentteamforge"));

    /// <summary>The pre-move Windows default (%LOCALAPPDATA%\AgentTeamForge) when it holds state and the new default does not.</summary>
    static string? LegacyWindowsStateDir(IReadOnlyDictionary<string, string> options, string dir)
    {
        if (!OperatingSystem.IsWindows() || options.ContainsKey("state-dir") || Directory.Exists(dir))
        {
            return null;
        }
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var legacy = localAppData.Length > 0 ? Path.Combine(localAppData, "AgentTeamForge") : null;
        return legacy is not null && File.Exists(Path.Combine(legacy, "profile.json")) ? legacy : null;
    }

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
            using (var file = new FileStream(temporary, PrivateFiles.Options(FileMode.CreateNew, FileAccess.Write)))
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
            using (var file = new FileStream(temporary, PrivateFiles.Options(FileMode.CreateNew, FileAccess.Write)))
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
        LaunchModeSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize(StateDirectory.ReadPrivateFile(path), SetupCommandJson.Default.LaunchModeSettings);
        }
        catch (JsonException)
        {
            throw new StateDirectoryException("launch_mode_invalid", $"invalid JSON in {path}; run atf setup or atf doctor to inspect configuration");
        }
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

    internal static string? FindExecutable(string name)
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
        // Run from "/" so mise never walks an ancestor chain containing the caller's (or an isolated HOME's parent) configs.
        if (!OperatingSystem.IsWindows())
        {
            info.WorkingDirectory = "/";
        }
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
            using var process = NonInteractiveProcess.Start(info) ?? throw new InvalidOperationException($"Cannot start {tool}");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var output = stdout.GetAwaiter().GetResult();
            var error = stderr.GetAwaiter().GetResult();
            return (process.ExitCode, process.ExitCode == 0 ? output : error.Length > 0 ? error : output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (127, "");
        }
    }

    static string QuotePowerShell(string value) => PowerShellText.Quote(value);
}

public sealed record LaunchModeSettings(string Mode, string? TerminalProvider = null, string? KittyAddress = null, string? KittyBinary = null)
{
    public int WebPort { get; init; } = SetupCommand.DefaultWebPort;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(LaunchModeSettings))]
public sealed partial class SetupCommandJson : JsonSerializerContext;
