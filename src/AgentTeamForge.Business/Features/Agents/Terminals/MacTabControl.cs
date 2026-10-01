using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AgentTeamForge.Business.Features.Processes;
using AgentTeamForge.DAL.Files;
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
                throw new BackendNotStartedException($"configured kitty remote control at {kittyAddress} is unavailable; "
                    + "start kitty with remote control listening there, or run atf setup --mode terminal again");
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
            Environment.ProcessPath ?? throw new IOException("atf executable path unavailable"), _codexHome,
            Environment.GetEnvironmentVariable("DOTNET_ROOT")), Encoding.UTF8, cancellationToken);
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        // The tab outlives a daemon restart; the next daemon finds a fenced job's tab by this record.
        if (launch.JobId is { } jobId)
        {
            using var job = new FileStream(Path.ChangeExtension(wrapper, ".job"), PrivateFiles.Options(FileMode.Create, FileAccess.Write));
            await job.WriteAsync(Encoding.ASCII.GetBytes(jobId), cancellationToken);
        }
        var start = LaunchInfo(provider, kittyAddress, kittyBinary, wrapper, launch.TabLabel ?? launch.AgentName);
        using var launcher = NonInteractiveProcess.Start(start) ?? throw new IOException("terminal launcher did not start");
        // osascript prints the new tab reference and kitty its window id; neither belongs in the daemon log.
        _ = launcher.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
        var error = launcher.StandardError.ReadToEndAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            while (true)
            {
                // A wrapper that already exited is classified by the evidence loop: its start error
                // fails the job, otherwise the turn is uncertain because the prompt was in its argv.
                if (TryReadSidecar(sidecar) is not null || File.Exists(startError))
                {
                    return;
                }
                if (launcher.HasExited && launcher.ExitCode != 0)
                {
                    throw LauncherFailure(launcher.ExitCode, await error);
                }
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("terminal agent did not report its PID within 12 seconds", ex);
        }
    }

    /// <summary>
    /// A refused Apple event proves Terminal never ran the wrapper; any other launcher failure may follow a
    /// tab that already opened, so it stays uncertain.
    /// </summary>
    internal static Exception LauncherFailure(int exitCode, string error) =>
        error.Contains("(-1743)", StringComparison.Ordinal) || error.Contains("(-1744)", StringComparison.Ordinal)
            ? new BackendNotStartedException("macOS did not allow AgentTeamForge to control Terminal (Automation permission). "
                + "Allow the app that started the ATF daemon to control Terminal in System Settings > Privacy & Security > Automation, "
                + "then retry; or run atf setup --mode terminal from kitty with remote control enabled, or choose --mode headless")
            : new IOException($"terminal launcher exited {exitCode}: {error.Trim()}");

    /// <summary>The owned tab, adopting its sidecar when the wrapper reported its PID after StartAsync gave up waiting.</summary>
    OwnedTab? Tab(InteractiveLaunch launch)
    {
        if (_tabs.TryGetValue(launch.AgentName, out var tab))
        {
            return tab;
        }
        var sidecar = Path.ChangeExtension(launch.BootstrapPath, ".pid");
        if (TryReadSidecar(sidecar) is not { } identity || Identity(identity.Pid, identity.Token) is IdentityState.Gone or IdentityState.Different)
        {
            return null;
        }
        return _tabs.GetOrAdd(launch.AgentName, new OwnedTab(identity.Pid, identity.Token, launch.BootstrapPath, sidecar));
    }

    public int? ProcessId(InteractiveLaunch launch) => Tab(launch)?.Pid;

    public bool IsAlive(InteractiveLaunch launch) =>
        Tab(launch) is { } tab && Identity(tab.Pid, tab.Token) is IdentityState.Ours or IdentityState.Unverified;

    public bool IsUnverified(InteractiveLaunch launch) =>
        Tab(launch) is { } tab && Identity(tab.Pid, tab.Token) == IdentityState.Unverified;

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
            // No PID yet: the tab may never have opened, or its /bin/sh may already be reading the wrapper.
            // The tombstone takes the PID's place, so terminal-token fails and the wrapper exits through its
            // start-error branch instead of running an agent nothing tracks any more.
            if ((!File.Exists(sidecar) && Tombstone(sidecar)) || IsTombstone(sidecar))
            {
                DeleteLaunchFiles(launch.BootstrapPath, keepTombstone: true);
                return;
            }
            // Either the wrapper just reported its PID (stopped below) or the sidecar is unverifiable (kept).
            if (TryReadSidecar(sidecar) is not { } identity) { return; }
            tab = new(identity.Pid, identity.Token, launch.BootstrapPath, sidecar);
        }
        if (Identity(tab.Pid, tab.Token) == IdentityState.Unverified)
        {
            throw new InvalidOperationException($"PID {tab.Pid} is alive but its creation token is unreadable; retaining terminal ownership");
        }
        // Codex starts tool shells in their own session; the agent's exit leaves them to launchd, where a platform
        // shell never shows the launch marker. Capture the tree while the agent still holds it.
        var tree = RunProcessSnapshots.Tree(tab.Pid, tab.Token);
        if (Identity(tab.Pid, tab.Token) == IdentityState.Ours)
        {
            DarwinProcess.SignalIfSame(tab.Pid, tab.Token, 15);
            WaitGone(tab);
            if (Identity(tab.Pid, tab.Token) == IdentityState.Ours)
            {
                DarwinProcess.SignalIfSame(tab.Pid, tab.Token, 9);
                // A killed agent stays visible until the terminal reaps it.
                WaitGone(tab);
            }
        }
        if (Identity(tab.Pid, tab.Token) is IdentityState.Ours or IdentityState.Unverified)
        {
            throw new InvalidOperationException($"PID {tab.Pid} is still live; retaining terminal ownership");
        }
        OrphanedBackendProcess.TerminateMarked([launch.AgentName]);
        RunProcessSnapshots.Signal(tree);
        _tabs.TryRemove(launch.AgentName, out _);
        DeleteLaunchFiles(tab.Wrapper);
    }

    static void WaitGone(OwnedTab tab)
    {
        for (var i = 0; i < 20 && Identity(tab.Pid, tab.Token) is IdentityState.Ours or IdentityState.Unverified; i++)
        {
            Thread.Sleep(100);
        }
    }

    /// <summary>The wrapper holds the prompt; it and its records go once nothing owned can still run from them.</summary>
    static void DeleteLaunchFiles(string wrapper, bool keepTombstone = false)
    {
        try
        {
            foreach (var extension in new[] { ".sh", ".start-error", ".session", LoginCheck, LoginCheckPartial })
            {
                File.Delete(Path.ChangeExtension(wrapper, extension));
            }
            File.Delete(Path.ChangeExtension(wrapper, ".job"));
            if (!keepTombstone) { File.Delete(Path.ChangeExtension(wrapper, ".pid")); }
        }
        catch (IOException) { }
    }

    const string TombstoneText = "stopped";

    /// <summary>Claims a launch's PID sidecar before its wrapper does; false when the wrapper (or anything else) got there first.</summary>
    static bool Tombstone(string sidecar)
    {
        try
        {
            using var file = new FileStream(sidecar, PrivateFiles.Options(FileMode.CreateNew, FileAccess.Write));
            file.Write(Encoding.ASCII.GetBytes(TombstoneText));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    static bool IsTombstone(string sidecar)
    {
        try
        {
            var file = new FileInfo(sidecar);
            return file.Exists && file.LinkTarget is null && file.Length == TombstoneText.Length
                && File.ReadAllText(sidecar, Encoding.ASCII) == TombstoneText;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The agent exited (or its PID now names another process), or its launch was stopped before it could start.</summary>
    public bool ProvenGone(InteractiveLaunch launch)
    {
        var sidecar = Path.ChangeExtension(launch.BootstrapPath, ".pid");
        return IsTombstone(sidecar)
            || (TryReadSidecar(sidecar) is { } identity && Identity(identity.Pid, identity.Token) is IdentityState.Gone or IdentityState.Different);
    }

    public void Busy(InteractiveLaunch launch)
    {
        try { File.Delete(Path.ChangeExtension(launch.BootstrapPath, ".session")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    const string LoginCheck = ".login-check", LoginCheckPartial = ".login-partial";
    const int MaxLoginCheckBytes = 4096;

    /// <summary>Pi's own verdict from the wrapper's `--list-models` probe: no provider has any credential.</summary>
    public BackendEvidence.AgentError? LoginBlocker(InteractiveLaunch launch)
    {
        if (launch.Kind != InteractiveAgentKind.Pi) { return null; }
        try
        {
            var file = new FileInfo(Path.ChangeExtension(launch.BootstrapPath, LoginCheck));
            if (!file.Exists || file.LinkTarget is not null) { return null; }
            using var stream = file.OpenRead();
            var buffer = new byte[MaxLoginCheckBytes];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return BackendLoginErrors.Inspect("pi", Encoding.UTF8.GetString(buffer, 0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Retained(InteractiveLaunch launch, string sessionId)
    {
        // Best effort: without the record a restarted daemon only loses the ability to reuse this agent.
        try
        {
            using var file = new FileStream(Path.ChangeExtension(launch.BootstrapPath, ".session"), PrivateFiles.Options(FileMode.Create, FileAccess.Write));
            file.Write(Encoding.UTF8.GetBytes(launch.Kind + "\n" + sessionId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Idle agents of <paramref name="kind"/> still running from before a daemon restart, by native session.</summary>
    internal static IEnumerable<(string SessionId, InteractiveLaunch Launch)> Survivors(string stateRoot, InteractiveAgentKind kind)
    {
        var directory = Path.Combine(stateRoot, "terminal");
        if (!Directory.Exists(directory)) { yield break; }
        foreach (var path in Directory.EnumerateFiles(directory, "*.launch.session"))
        {
            string[] fields;
            try
            {
                var file = new FileInfo(path);
                if (file.LinkTarget is not null || file.Length > 512 || File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite)) { continue; }
                fields = File.ReadAllText(path).Split('\n');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (fields is not [var recordedKind, { Length: > 0 } sessionId] || recordedKind != kind.ToString()
                || TryReadSidecar(Path.ChangeExtension(path, ".pid")) is not { } identity
                || Identity(identity.Pid, identity.Token) is not (IdentityState.Ours or IdentityState.Unverified))
            {
                continue;
            }
            var agentName = Path.GetFileName(path)[..^".launch.session".Length];
            yield return (sessionId, new InteractiveLaunch(kind, agentName, stateRoot, sessionId, null, Path.ChangeExtension(path, ".sh")));
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
            if (IsTombstone(sidecar))
            {
                // A stopped launch's wrapper proved it exited (start-error), or never ran in a day: nothing can claim the PID now.
                if (File.Exists(Path.ChangeExtension(sidecar, ".start-error"))
                    || DateTime.UtcNow - File.GetLastWriteTimeUtc(sidecar) > TimeSpan.FromDays(1))
                {
                    DeleteLaunchFiles(sidecar);
                }
                continue;
            }
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
                // Processes the exited agent left behind, recorded while it ran (RunProcessSnapshots).
                var name = Path.GetFileName(sidecar);
                if (name.EndsWith(".launch.pid", StringComparison.Ordinal) && name[..^".launch.pid".Length] is var agentName
                    && HerdrOwnedSessions.ValidAgentName(agentName))
                {
                    OrphanedBackendProcess.TerminateMarked([agentName]);
                }
                DeleteLaunchFiles(sidecar);
            }
        }
        // A wrapper that never reported a PID belongs to a launch the previous daemon already settled;
        // tombstone its PID first, since a tab may still be about to run it.
        foreach (var wrapper in Directory.EnumerateFiles(directory, "*.launch.sh"))
        {
            if (!File.Exists(Path.ChangeExtension(wrapper, ".pid")) && Tombstone(Path.ChangeExtension(wrapper, ".pid")))
            {
                DeleteLaunchFiles(wrapper, keepTombstone: true);
            }
        }
    }

    internal static string WrapperText(InteractiveLaunch launch, string prompt, string sidecar, string atfBinary, string? codexHome = null, string? dotnetRoot = null)
    {
        var args = WtTabControl.AgentArguments(launch, prompt);
        var executable = FindExecutable(args[0]) ?? args[0];
        var command = string.Join(' ', new[] { executable }.Concat(args.Skip(1)).Select(ShellQuote));
        var trust = InteractiveAgentCommand.WorkspaceTrustEnvironment(launch.Kind);
        var identityNames = string.Join(' ', LaunchEnvironment.IdentityNames);
        var identityPrefixes = string.Join('|', LaunchEnvironment.IdentityPrefixes);
        var startError = ShellQuote(Path.ChangeExtension(sidecar, ".start-error"));
        return "#!/bin/sh\nset -eu\n" +
            "unset " + identityNames + "\n" +
            "for name in $(env | cut -d= -f1 | grep -E '^(" + identityPrefixes + ")' || :); do unset \"$name\"; done\n" +
            (trust is { } env ? "export " + env.Name + "=" + ShellQuote(env.Value) + "\n" : "") +
            // A framework-dependent atf needs the daemon's runtime; the tab starts from the terminal's environment.
            (dotnetRoot is { Length: > 0 } ? "export DOTNET_ROOT=" + ShellQuote(dotnetRoot) + "\n" : "") +
            (launch.Kind == InteractiveAgentKind.Codex ? CodexHomeLine(codexHome) : "") +
            (launch.Kind == InteractiveAgentKind.Pi && InteractiveAgentCommand.ManagedConfigPath(launch) is { } config && File.Exists(config)
                ? "export PI_MCP_CONFIG_MODE=exclusive\n" : "") +
            "cd " + ShellQuote(launch.WorkingDirectory) + "\n" +
            "export ATF_RUN_CORRELATION=" + ShellQuote(launch.AgentName) + "\n" +
            "if ! " + ShellQuote(atfBinary) + " terminal-token --pid \"$$\" --sidecar " + ShellQuote(sidecar) + "; then\n" +
            "  printf '%s\\n' " + ShellQuote("atf terminal-token failed in the terminal tab; " + atfBinary + " did not run there") + " > " + startError + "\n" +
            "  exit 0\n" +
            "fi\n" +
            "if [ ! -x " + ShellQuote(executable) + " ]; then\n" +
            "  printf '%s\\n' 'agent executable is unavailable' > " + startError + "\n" +
            "  exit 0\n" +
            "fi\n" +
            (launch.Kind == InteractiveAgentKind.Pi ? PiLoginProbe(executable, args, sidecar) : "") +
            "exec " + command + "\n";
    }

    /// <summary>
    /// A signed-out interactive Pi shows its error only on screen and never records the prompt. Its own
    /// `--list-models` check, run beside the TUI with the same environment and arguments, says so in a file.
    /// </summary>
    internal static string PiLoginProbe(string executable, IReadOnlyList<string> args, string sidecar)
    {
        var partial = ShellQuote(Path.ChangeExtension(sidecar, LoginCheckPartial));
        // The prompt is the last argument; the rest select the same model, extensions and approval.
        var probe = string.Join(' ', new[] { executable }.Concat(args.Skip(1).SkipLast(1)).Append("--offline").Append("--list-models").Select(ShellQuote));
        return "(umask 077; " + probe + " </dev/null >" + partial + " 2>&1 && /bin/mv -f " + partial + " "
            + ShellQuote(Path.ChangeExtension(sidecar, LoginCheck)) + ") </dev/null >/dev/null 2>&1 &\n";
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
            var info = new ProcessStartInfo(kitty) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
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
        var terminal = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
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
            // A backgrounded kitty can take several seconds to answer; its own client waits 10.
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                return false;
            }
            // WaitForExit does not wait for stream reads; `kitty @ ls` output is large enough to still be in flight.
            return process.ExitCode == 0 && Task.WaitAll([output, error], TimeSpan.FromSeconds(5));
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
public sealed class MacInteractiveBackend : IJobBackend, IInteractiveSessionStop, IDisposable
{
    public MacInteractiveBackend(InteractiveAgentKind kind, string stateRoot, string provider, string? kittyAddress, string? kittyBinary, TimeSpan? idleTimeout = null, Func<InteractiveRetentionSettings>? retentionSettings = null)
        : this(Adopt(new(new MacTabControl(provider, kittyAddress, kittyBinary), new InteractiveTranscriptReader(), kind, stateRoot, "terminal",
            configPreflight: InteractiveAgentPreflight.CheckCurrent, idleTimeout: idleTimeout, retentionSettings: retentionSettings), stateRoot, kind))
    { }

    internal MacInteractiveBackend(WtInteractiveBackend tabs) => Tabs = tabs;

    /// <summary>The tab backend this wraps; the dispatcher drives macOS tabs through it exactly as Windows tabs.</summary>
    internal WtInteractiveBackend Tabs { get; }

    static WtInteractiveBackend Adopt(WtInteractiveBackend backend, string stateRoot, InteractiveAgentKind kind)
    {
        foreach (var (sessionId, launch) in MacTabControl.Survivors(stateRoot, kind))
        {
            backend.AdoptRetained(sessionId, launch);
        }
        return backend;
    }

    public void Dispose() => Tabs.Dispose();
    public IBackendRun Start(BackendRequest request) => Tabs.Start(request);
    public bool HasIdleSession(string sessionId) => Tabs.HasIdleSession(sessionId);
    public bool? HasLiveSession(string sessionId) => Tabs.HasLiveSession(sessionId);
    public bool StopIdleSession(string sessionId) => Tabs.StopIdleSession(sessionId);
    public void ReleaseNativeTurn(string sessionId) => Tabs.ReleaseNativeTurn(sessionId);
    public void StopAllIdleSessions() => Tabs.StopAllIdleSessions();

    public static void Recover(string stateRoot, Action<string> log) => MacTabControl.Recover(stateRoot, log);
}
