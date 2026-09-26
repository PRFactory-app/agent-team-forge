using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>Terminal.app or kitty hosts an agent process; only its PID and Darwin start token are owned.</summary>
internal sealed class MacTabControl(string provider, string? kittyAddress, string? kittyBinary) : IWtTabControl
{
    readonly ConcurrentDictionary<string, OwnedTab> _tabs = [];

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
        if (FindExecutable(executable) is null)
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

        await File.WriteAllTextAsync(wrapper, WrapperText(launch, prompt, sidecar,
            Environment.ProcessPath ?? throw new IOException("atf executable path unavailable")), Encoding.UTF8, cancellationToken);
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var start = LaunchInfo(provider, kittyAddress, kittyBinary, wrapper, launch.AgentName);
        using var launcher = Process.Start(start) ?? throw new IOException("terminal launcher did not start");
        var error = launcher.StandardError.ReadToEndAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            while (true)
            {
                if (TryReadSidecar(sidecar) is { } identity && DarwinProcess.CreationToken(identity.Pid) == identity.Token)
                {
                    _tabs[launch.AgentName] = new(identity.Pid, identity.Token, wrapper, sidecar);
                    await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
                    if (!IsAlive(launch))
                    {
                        _tabs.TryRemove(launch.AgentName, out _);
                        throw new IOException("terminal agent exited during startup");
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
        _tabs.TryGetValue(launch.AgentName, out var tab) && DarwinProcess.CreationToken(tab.Pid) == tab.Token;

    public void StopOwned(InteractiveLaunch launch)
    {
        if (!_tabs.TryGetValue(launch.AgentName, out var tab))
        {
            return;
        }
        if (DarwinProcess.CreationToken(tab.Pid) == tab.Token)
        {
            DarwinProcess.SignalIfSame(tab.Pid, tab.Token, 15);
            for (var i = 0; i < 20 && DarwinProcess.CreationToken(tab.Pid) == tab.Token; i++)
            {
                Thread.Sleep(100);
            }
            if (DarwinProcess.CreationToken(tab.Pid) == tab.Token)
            {
                DarwinProcess.SignalIfSame(tab.Pid, tab.Token, 9);
            }
            OrphanedBackendProcess.TerminateMarked([launch.AgentName]);
        }
        _tabs.TryRemove(launch.AgentName, out _);
        if (DarwinProcess.CreationToken(tab.Pid) != tab.Token)
        {
            try { File.Delete(tab.Sidecar); File.Delete(tab.Wrapper); }
            catch (IOException) { }
        }
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
            else if (DarwinProcess.CreationToken(identity.Value.Pid) == identity.Value.Token)
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

    internal static string WrapperText(InteractiveLaunch launch, string prompt, string sidecar, string atfBinary)
    {
        var args = WtTabControl.AgentArguments(launch, prompt);
        var command = string.Join(' ', new[] { FindExecutable(args[0]) ?? args[0] }.Concat(args.Skip(1)).Select(ShellQuote));
        var trust = InteractiveAgentCommand.WorkspaceTrustEnvironment(launch.Kind);
        return "#!/bin/sh\nset -eu\n" +
            "unset CLAUDECODE CLAUDE_PID CODEX_THREAD_ID CLAUDE_CODE_MESSAGING_SOCKET CLAUDE_CODE_MESSAGING_TOKEN\n" +
            (trust is { } env ? "export " + env.Name + "=" + ShellQuote(env.Value) + "\n" : "") +
            "cd " + ShellQuote(launch.WorkingDirectory) + "\n" +
            "export ATF_RUN_CORRELATION=" + ShellQuote(launch.AgentName) + "\n" +
            ShellQuote(atfBinary) + " terminal-token --pid \"$$\" --sidecar " + ShellQuote(sidecar) + "\n" +
            "exec " + command + "\n";
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

            using var process = Process.Start(info);
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
public sealed class MacInteractiveBackend(InteractiveAgentKind kind, string stateRoot, string provider, string? kittyAddress, string? kittyBinary) : IJobBackend
{
    readonly WtInteractiveBackend _backend = new(new MacTabControl(provider, kittyAddress, kittyBinary),
        new InteractiveTranscriptReader(), kind, stateRoot, "terminal");

    public IBackendRun Start(BackendRequest request) => _backend.Start(request);

    public static void Recover(string stateRoot, Action<string> log) => MacTabControl.Recover(stateRoot, log);
}
