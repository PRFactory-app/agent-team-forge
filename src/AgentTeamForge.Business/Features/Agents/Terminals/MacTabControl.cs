using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AgentTeamForge.Business.Features.Processes;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>Terminal.app or kitty hosts an agent process; only its PID and Darwin start token are owned.</summary>
internal sealed class MacTabControl(string provider, string? kittyAddress, string? kittyBinary) : IWtTabControl
{
    internal enum IdentityState { Gone, Ours, Unverified, Different }

    internal static IdentityState Identity(ulong expected, ulong? current, bool pidAlive) => !pidAlive ? IdentityState.Gone : current switch
    {
        { } token when token == expected => IdentityState.Ours,
        not null => IdentityState.Different,
        _ => IdentityState.Unverified,
    };

    static IdentityState Identity(int pid, ulong expected) => Identity(expected, DarwinProcess.CreationToken(pid), DarwinProcess.PidAlive(pid));

    readonly ConcurrentDictionary<string, OwnedTab> _tabs = [];
    readonly string? _codexHome = CodexPaths.LaunchHome(Environment.GetEnvironmentVariable, Environment.CurrentDirectory);

    public void Preflight(InteractiveAgentKind kind)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new BackendNotStartedException("macOS terminal mode requires macOS");
        }
        if (provider == "kitty")
        {
            if (kittyAddress is null || kittyBinary is null || !File.Exists(kittyBinary) || !KittyResponds(kittyBinary, kittyAddress))
            {
                throw new BackendNotStartedException("configured kitty remote control is unavailable");
            }
        }
        else if (provider != "terminal" || !File.Exists("/usr/bin/osascript"))
        {
            throw new BackendNotStartedException("Terminal.app launcher is unavailable");
        }
        var executable = kind switch { InteractiveAgentKind.Claude => "claude", InteractiveAgentKind.Codex => "codex", _ => "pi" };
        if (FindExecutable(executable) is not { } binary || !IsExecutable(binary))
        {
            throw new BackendNotStartedException($"{executable} is unavailable");
        }
    }

    public async Task StartAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken)
    {
        Preflight(launch.Kind);
        var wrapper = launch.BootstrapPath;
        var sidecar = Path.ChangeExtension(wrapper, ".pid");
        var directory = Path.GetDirectoryName(wrapper)!;
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (File.Exists(sidecar))
        {
            throw new IOException("terminal ownership sidecar already exists");
        }
        var startError = Path.ChangeExtension(wrapper, ".start-error");
        if (File.Exists(startError)) { File.Delete(startError); }

        await File.WriteAllTextAsync(wrapper, WrapperText(launch, prompt, sidecar,
            Environment.ProcessPath ?? throw new IOException("atf executable path unavailable"), _codexHome), Encoding.UTF8, cancellationToken);
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var start = LaunchInfo(provider, kittyAddress, kittyBinary, wrapper, launch.TabLabel ?? launch.AgentName);
        using var launcher = NonInteractiveProcess.Start(start) ?? throw new IOException("terminal launcher did not start");
        var error = launcher.StandardError.ReadToEndAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            while (true)
            {
                if (TryReadSidecar(sidecar) is { } identity)
                {
                    if (Identity(identity.Pid, identity.Token) is IdentityState.Gone or IdentityState.Different) { return; }
                    _tabs[launch.AgentName] = new(identity.Pid, identity.Token, wrapper, sidecar);
                    await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
                    if (!IsAlive(launch))
                    {
                        return;
                    }
                    return;
                }
                if (launcher.HasExited && launcher.ExitCode != 0)
                {
                    throw new IOException($"terminal launcher exited {launcher.ExitCode}: {await error}");
                }
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("terminal agent did not report its PID within 12 seconds", ex);
        }
    }

    public int? ProcessId(InteractiveLaunch launch) => _tabs.TryGetValue(launch.AgentName, out var tab) ? tab.Pid : null;

    public bool IsAlive(InteractiveLaunch launch) =>
        _tabs.TryGetValue(launch.AgentName, out var tab) && Identity(tab.Pid, tab.Token) is IdentityState.Ours or IdentityState.Unverified;

    public bool IsUnverified(InteractiveLaunch launch) =>
        _tabs.TryGetValue(launch.AgentName, out var tab) && Identity(tab.Pid, tab.Token) == IdentityState.Unverified;

    public string? StartFailure(InteractiveLaunch launch)
    {
        var error = Path.ChangeExtension(launch.BootstrapPath, ".start-error");
        try
        {
            var file = new FileInfo(error);
            if (file.Exists && file.LinkTarget is null && file.Length <= 4096)
            {
                return "interactive agent could not start: " + File.ReadAllText(error).Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    public bool WrapperExited(InteractiveLaunch launch) =>
        TryReadSidecar(Path.ChangeExtension(launch.BootstrapPath, ".pid")) is { } identity
        && Identity(identity.Pid, identity.Token) is IdentityState.Gone or IdentityState.Different;

    public void StopOwned(InteractiveLaunch launch)
    {
        if (!_tabs.TryGetValue(launch.AgentName, out var tab))
        {
            var sidecar = Path.ChangeExtension(launch.BootstrapPath, ".pid");
            if (TryReadSidecar(sidecar) is not { } identity) { return; }
            tab = new(identity.Pid, identity.Token, launch.BootstrapPath, sidecar);
        }
        if (Identity(tab.Pid, tab.Token) == IdentityState.Unverified)
        {
            throw new InvalidOperationException($"PID {tab.Pid} is alive but its creation token is unreadable; retaining terminal ownership");
        }
        if (Identity(tab.Pid, tab.Token) == IdentityState.Ours)
        {
            DarwinProcess.SignalIfSame(tab.Pid, tab.Token, 15);
            for (var i = 0; i < 20 && Identity(tab.Pid, tab.Token) is IdentityState.Ours or IdentityState.Unverified; i++)
            {
                Thread.Sleep(100);
            }
            if (Identity(tab.Pid, tab.Token) == IdentityState.Ours)
            {
                DarwinProcess.SignalIfSame(tab.Pid, tab.Token, 9);
            }
        }
        if (Identity(tab.Pid, tab.Token) is IdentityState.Ours or IdentityState.Unverified)
        {
            throw new InvalidOperationException($"PID {tab.Pid} is still live; retaining terminal ownership");
        }
        OrphanedBackendProcess.TerminateMarked([launch.AgentName]);
        _tabs.TryRemove(launch.AgentName, out _);
        try { File.Delete(tab.Sidecar); File.Delete(tab.Wrapper); File.Delete(Path.ChangeExtension(tab.Wrapper, ".start-error")); }
        catch (IOException) { }
    }

    /// <summary>Find surviving owned processes after a daemon crash without stopping a live TUI.</summary>
    public static void Recover(string stateRoot, Action<string> log)
    {
        var directory = Path.Combine(stateRoot, "terminal");
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var sidecar in Directory.EnumerateFiles(directory, "*.pid"))
        {
            var identity = TryReadSidecar(sidecar);
            if (identity is null)
            {
                log($"terminal ownership sidecar unreadable: {sidecar}; left for reconciliation");
            }
            else if (Identity(identity.Value.Pid, identity.Value.Token) is IdentityState.Ours or IdentityState.Unverified)
            {
                log($"terminal agent still live after restart: PID {identity.Value.Pid}; left for reconciliation");
            }
            else
            {
                try { File.Delete(sidecar); File.Delete(Path.ChangeExtension(sidecar, ".sh")); }
                catch (IOException) { }
            }
        }
    }

    internal static string WrapperText(InteractiveLaunch launch, string prompt, string sidecar, string atfBinary, string? codexHome = null)
    {
        var args = WtTabControl.AgentArguments(launch, prompt);
        var executable = FindExecutable(args[0]) ?? args[0];
        var command = string.Join(' ', new[] { executable }.Concat(args.Skip(1)).Select(ShellQuote));
        var trust = InteractiveAgentCommand.WorkspaceTrustEnvironment(launch.Kind);
        var identityNames = string.Join(' ', LaunchEnvironment.IdentityNames);
        var identityPrefixes = string.Join('|', LaunchEnvironment.IdentityPrefixes);
        return "#!/bin/sh\nset -eu\n" +
            "unset " + identityNames + "\n" +
            "for name in $(env | cut -d= -f1 | grep -E '^(" + identityPrefixes + ")' || :); do unset \"$name\"; done\n" +
            (trust is { } env ? "export " + env.Name + "=" + ShellQuote(env.Value) + "\n" : "") +
            (launch.Kind == InteractiveAgentKind.Codex ? CodexHomeLine(codexHome) : "") +
            (launch.Kind == InteractiveAgentKind.Pi && InteractiveAgentCommand.ManagedConfigPath(launch) is { } config && File.Exists(config)
                ? "export PI_MCP_CONFIG_MODE=exclusive\n" : "") +
            "cd " + ShellQuote(launch.WorkingDirectory) + "\n" +
            "export ATF_RUN_CORRELATION=" + ShellQuote(launch.AgentName) + "\n" +
            ShellQuote(atfBinary) + " terminal-token --pid \"$$\" --sidecar " + ShellQuote(sidecar) + "\n" +
            "if [ ! -x " + ShellQuote(executable) + " ]; then\n" +
            "  printf '%s\\n' 'agent executable is unavailable' > " + ShellQuote(Path.ChangeExtension(sidecar, ".start-error")) + "\n" +
            "  exit 0\n" +
            "fi\n" +
            "exec " + command + "\n";
    }

    static string CodexHomeLine(string? codexHome)
    {
        var home = codexHome ?? CodexPaths.LaunchHome(Environment.GetEnvironmentVariable, Environment.CurrentDirectory);
        return home is null ? "unset CODEX_HOME\n" : "export CODEX_HOME=" + ShellQuote(home) + "\n";
    }

    internal static string ShellQuote(string value) => value.Contains('\0')
        ? throw new ArgumentException("shell arguments cannot contain NUL", nameof(value))
        : "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    internal static ProcessStartInfo LaunchInfo(string provider, string? address, string? kitty, string wrapper, string title)
    {
        if (provider == "kitty")
        {
            if (address is null || kitty is null)
            {
                throw new InvalidOperationException("configured kitty endpoint is missing");
            }
            var info = new ProcessStartInfo(kitty) { UseShellExecute = false, RedirectStandardError = true };
            foreach (var arg in new[] { "@", "--to", address, "launch", "--type=tab", "--tab-title", title, "/bin/sh", wrapper })
            {
                info.ArgumentList.Add(arg);
            }

            return info;
        }
        if (provider != "terminal")
        {
            throw new InvalidOperationException("unknown macOS terminal provider");
        }
        // Terminal runs the text in the user's login shell (zsh, bash or fish). Plain single quotes
        // mean the same in all of them only without backslashes or control characters.
        if (wrapper.Any(c => c == '\\' || char.IsControl(c)))
        {
            throw new BackendNotStartedException("Terminal.app wrapper path contains a backslash or control character");
        }
        // The command reaches AppleScript as argv, never as script text.
        var terminal = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in new[] { "-e", "on run argv", "-e", "tell application \"Terminal\" to do script (item 1 of argv)", "-e", "end run",
            "exec /bin/sh " + ShellQuote(wrapper) })
        {
            terminal.ArgumentList.Add(arg);
        }
        return terminal;
    }

    internal static bool KittyResponds(string binary, string address)
    {
        try
        {
            var info = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "@", "--to", address, "ls" })
            {
                info.ArgumentList.Add(arg);
            }

            using var process = NonInteractiveProcess.Start(info);
            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                return false;
            }
            return process.ExitCode == 0 && output.IsCompleted && error.IsCompleted;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
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

    static bool IsExecutable(string path)
    {
        try
        {
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    static (int Pid, ulong Token)? TryReadSidecar(string path)
    {
        try
        {
            if (File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
            {
                return null;
            }
            var fields = File.ReadAllText(path).Trim().Split(' ');
            return fields is [var pid, var token]
                && int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPid) && parsedPid > 0
                && ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedToken) && parsedToken > 0
                ? (parsedPid, parsedToken) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    sealed record OwnedTab(int Pid, ulong Token, string Wrapper, string Sidecar);
}

/// <summary>Uses the Windows tab backend's native transcript and evidence loop with a macOS tab host.</summary>
public sealed class MacInteractiveBackend(InteractiveAgentKind kind, string stateRoot, string provider, string? kittyAddress, string? kittyBinary, TimeSpan? idleTimeout = null, Func<InteractiveRetentionSettings>? retentionSettings = null) : IJobBackend, IInteractiveSessionStop, IDisposable
{
    readonly WtInteractiveBackend _backend = new(new MacTabControl(provider, kittyAddress, kittyBinary),
        new InteractiveTranscriptReader(), kind, stateRoot, "terminal", configPreflight: InteractiveAgentPreflight.CheckCurrent, idleTimeout: idleTimeout, retentionSettings: retentionSettings);

    public void Dispose() => _backend.Dispose();
    public IBackendRun Start(BackendRequest request) => _backend.Start(request);
    public bool HasIdleSession(string sessionId) => _backend.HasIdleSession(sessionId);
    public bool? HasLiveSession(string sessionId) => _backend.HasLiveSession(sessionId);
    public bool StopIdleSession(string sessionId) => _backend.StopIdleSession(sessionId);
    public void ReleaseNativeTurn(string sessionId) => _backend.ReleaseNativeTurn(sessionId);
    public void StopAllIdleSessions() => _backend.StopAllIdleSessions();

    public static void Recover(string stateRoot, Action<string> log) => MacTabControl.Recover(stateRoot, log);
}
