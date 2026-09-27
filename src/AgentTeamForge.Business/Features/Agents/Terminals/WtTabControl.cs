using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>Windows Terminal hands off the tab; the wrapper PID and creation time own its lifetime.</summary>
internal sealed class WtTabControl : IWtTabControl
{
    readonly ConcurrentDictionary<string, OwnedTab> _tabs = [];
    readonly string? _codexHome = CodexPaths.LaunchHome(Environment.GetEnvironmentVariable, Environment.CurrentDirectory);

    public void Preflight(InteractiveAgentKind kind)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new BackendNotStartedException("wt interactive mode requires Windows");
        }
        if (FindExecutable("powershell.exe") is null)
        {
            throw new BackendNotStartedException("Windows PowerShell is unavailable");
        }
        if (kind == InteractiveAgentKind.Pi)
        {
            ValidateWindowsAgentBinary(WindowsPiLauncher()[0]);
        }
        else
        {
            var binary = WindowsAgentBinary(kind == InteractiveAgentKind.Claude ? "claude" : "codex");
            EnsureInteractiveCodexNative(kind, binary);
            ValidateWindowsAgentBinary(binary);
        }
    }

    public async Task StartAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken)
    {
        Preflight(launch.Kind);
        var wrapper = launch.BootstrapPath;
        var sidecar = Path.ChangeExtension(wrapper, ".pid");
        var startError = Path.ChangeExtension(wrapper, ".start-error");
        var promptFile = Path.ChangeExtension(wrapper, ".prompt.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(wrapper)!);
        if (File.Exists(sidecar))
        {
            File.Delete(sidecar);
        }
        if (File.Exists(startError)) { File.Delete(startError); }

        if (launch.JobId is { } jobId)
        {
            await File.WriteAllTextAsync(Path.ChangeExtension(wrapper, ".job"), jobId, Encoding.ASCII, cancellationToken);
        }
        await File.WriteAllTextAsync(promptFile, prompt, Encoding.UTF8, cancellationToken);
        if (launch.Kind == InteractiveAgentKind.Codex)
        {
            await File.WriteAllTextAsync(HookScript(launch), CodexHookScript(launch), Encoding.UTF8, cancellationToken);
            await File.WriteAllTextAsync(HookLauncher(launch), CodexHookLauncher(launch), Encoding.ASCII, cancellationToken);
        }
        await File.WriteAllBytesAsync(wrapper, WrapperBytes(launch, prompt, sidecar, _codexHome), cancellationToken);

        if (FindExecutable("wt.exe") is not { } wt)
        {
            await StartConsoleAsync(launch, wrapper, sidecar, cancellationToken);
            return;
        }
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (localAppData.Length > 0 && Path.GetFullPath(wrapper).StartsWith(
            Path.TrimEndingDirectorySeparator(localAppData) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            // Windows Terminal is MSIX-packaged; its tabs see a virtualized AppData\Local and cannot find the wrapper.
            throw new IOException($"state directory is under {localAppData}, which Windows Terminal tabs cannot read; use a --state-dir outside it");
        }
        var args = new[] { "-w", "wt-atf", "nt", "--title", launch.AgentName,
            "--suppressApplicationTitle", "--", "powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", wrapper };
        WindowsConsoleProcess? launcher = null;
        try
        {
            try { launcher = WindowsConsoleProcess.Start(wt, args, newConsole: false); }
            catch (System.ComponentModel.Win32Exception) { }
            if (launcher is not null)
            {
                await AwaitTabAsync(launch, wrapper, sidecar, () => (launcher.HasExited, launcher.HasExited ? launcher.ExitCode : 0), cancellationToken);
                return;
            }
        }
        finally { launcher?.Dispose(); }
        // Keep the existing console fallback when wt.exe did not return a launcher.
        // A wrapper whose PID was seen is never retried; its agent may have started.
        // A fresh console remains interactive; never switch to a pipe/headless run.
        await StartConsoleAsync(launch, wrapper, sidecar, cancellationToken);
    }

    async Task StartConsoleAsync(InteractiveLaunch launch, string wrapper, string sidecar, CancellationToken token)
    {
        WindowsConsoleProcess started;
        try { started = WindowsConsoleProcess.StartConsole(wrapper); }
        catch (System.ComponentModel.Win32Exception ex) { throw new IOException("interactive console could not start", ex); }
        using var process = started;
        await AwaitTabAsync(launch, wrapper, sidecar, () => (process.HasExited, process.ExitCode), token);
    }

    async Task AwaitTabAsync(InteractiveLaunch launch, string wrapper, string sidecar, Func<(bool Exited, int Code)> status, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = InteractiveStartup.Timeout;
        deadline.CancelAfter(timeout);
        try
        {
            await PollTabAsync(launch, wrapper, sidecar, status, deadline.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"interactive agent did not report its PID within {timeout.TotalSeconds:0} seconds (wrapper {wrapper})", ex);
        }
    }

    async Task PollTabAsync(InteractiveLaunch launch, string wrapper, string sidecar, Func<(bool Exited, int Code)> status, CancellationToken token)
    {
        while (true)
        {
            if (TryReadOwned(sidecar, wrapper) is { } tab)
            {
                if (TryIdentity(tab.Pid) != tab.Created) { return; }
                _tabs[launch.AgentName] = tab;
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                if (!IsAlive(launch))
                {
                    // The wrapper ran. A retry could launch the agent twice; evidence
                    // distinguishes its proven pre-ack exit from an uncertain handoff.
                    return;
                }
                return;
            }
            var (exited, code) = status();
            if (exited && code != 0)
            {
                throw new IOException($"interactive launcher exited {code}");
            }
            await Task.Delay(100, token);
        }
    }

    OwnedTab? Owned(InteractiveLaunch launch)
    {
        if (_tabs.TryGetValue(launch.AgentName, out var existing) && TryIdentity(existing.Pid) == existing.Created)
        {
            return existing;
        }
        // The wrapper may write its sidecar after the launch wait expired.
        var sidecar = Path.ChangeExtension(launch.BootstrapPath, ".pid");
        var found = TryReadOwned(sidecar, launch.BootstrapPath);
        if (found is null || TryIdentity(found.Pid) != found.Created) { return null; }
        _tabs[launch.AgentName] = found;
        return found;
    }

    public int? ProcessId(InteractiveLaunch launch) => Owned(launch)?.Pid;

    public bool IsAlive(InteractiveLaunch launch) => Owned(launch) is not null;

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
        TryReadOwned(Path.ChangeExtension(launch.BootstrapPath, ".pid"), launch.BootstrapPath) is { } tab
        && TryIdentity(tab.Pid) != tab.Created;

    public void StopOwned(InteractiveLaunch launch)
    {
        var tab = Owned(launch) ?? TryReadOwned(Path.ChangeExtension(launch.BootstrapPath, ".pid"), launch.BootstrapPath);
        if (tab is null)
        {
            return;
        }

        StopOwned(tab);
        _tabs.TryRemove(launch.AgentName, out _);
    }

    static bool StopOwned(OwnedTab tab)
    {
        if (TryIdentity(tab.Pid) == tab.Created)
        {
            // Ending the recorded agent lets the wrapper close its kill-on-close job
            // (taking descendants down) and exit 0, which closes its tab. Killing the
            // wrapper is the fallback; its job handle closes with it.
            if (TryReadOwned(Path.ChangeExtension(tab.Wrapper, ".agent"), tab.Wrapper) is { } agent)
            {
                KillIfSame(agent.Pid, agent.Created);
                WaitForExit(tab);
            }
            if (TryIdentity(tab.Pid) == tab.Created)
            {
                KillIfSame(tab.Pid, tab.Created);
                WaitForExit(tab);
            }
        }
        if (TryIdentity(tab.Pid) == tab.Created)
        {
            // Preserve the sidecar so the next daemon can retry owned cleanup.
            return false;
        }
        try { File.Delete(tab.Sidecar); } catch (IOException) { }
        try { File.Delete(tab.Wrapper); } catch (IOException) { }
        try { File.Delete(Path.ChangeExtension(tab.Wrapper, ".prompt.txt")); } catch (IOException) { }
        try { File.Delete(Path.ChangeExtension(tab.Wrapper, ".job")); } catch (IOException) { }
        try { File.Delete(Path.ChangeExtension(tab.Wrapper, ".agent")); } catch (IOException) { }
        try { File.Delete(Path.ChangeExtension(tab.Wrapper, ".hook.ps1")); } catch (IOException) { }
        try { File.Delete(Path.ChangeExtension(tab.Wrapper, ".hook.cmd")); } catch (IOException) { }
        try { File.Delete(Path.ChangeExtension(tab.Wrapper, ".start-error")); } catch (IOException) { }
        return true;
    }

    internal static byte[] WrapperBytes(InteractiveLaunch launch, string prompt, string sidecar, string? codexHome = null)
    {
        var args = AgentArguments(launch, prompt);
        var shim = args[0].EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var shimCommand = shim ? "& " + Quote(args[0]) + " "
            + string.Join(' ', args.Skip(1).Select(WindowsCliLaunch.ShimArgument).Select(Quote)) : null;
        var executable = shim ? "powershell.exe" : args[0];
        var arguments = shim ? "-NoProfile -EncodedCommand "
            + Convert.ToBase64String(Encoding.Unicode.GetBytes(shimCommand!)) : CommandLine(args.Skip(1));
        var identityNames = string.Join(',', LaunchEnvironment.IdentityNames.Select(Quote));
        var identityPrefixes = string.Join(" -or ", LaunchEnvironment.IdentityPrefixes.Select(prefix =>
            "$_.Name.StartsWith(" + Quote(prefix) + ", [System.StringComparison]::OrdinalIgnoreCase)"));
        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            // A tab attached to an existing WT window inherits that window's environment.
            // Remove the caller's agent/session identity before starting a child agent.
            "Get-ChildItem Env: | Where-Object { $_.Name -in @(" + identityNames + ") -or " + identityPrefixes
                + " } | ForEach-Object { Remove-Item -LiteralPath ('Env:' + $_.Name) }",
            "Set-Location -LiteralPath " + Quote(launch.WorkingDirectory),
            "($PID.ToString() + '|' + (Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks) | Out-File -FilePath " + Quote(sidecar) + " -Encoding ascii",
            // Windows PowerShell 5.1 does not escape embedded double quotes when it
            // passes arguments to a native program, which splits or rewrites the
            // prompt. Start the agent with a pre-built command line instead.
            "$start = New-Object System.Diagnostics.ProcessStartInfo",
            "$start.FileName = " + Quote(executable),
            "$start.Arguments = " + Quote(arguments),
            "$start.WorkingDirectory = " + Quote(launch.WorkingDirectory),
            "$start.UseShellExecute = $false",
            // The wrapper owns this handle. Closing the tab or stopping the
            // recorded agent kills its descendants without daemon ownership.
            WindowsAgentJobScript.Replace("__AGENT_SIDECAR__", Quote(Path.ChangeExtension(sidecar, ".agent")), StringComparison.Ordinal)
                .Replace("__START_ERROR__", Quote(Path.ChangeExtension(sidecar, ".start-error")), StringComparison.Ordinal),
            "exit 0"
        };
        if (InteractiveAgentCommand.WorkspaceTrustEnvironment(launch.Kind) is { } trust)
        {
            lines.Insert(2, "$env:" + trust.Name + " = " + Quote(trust.Value));
        }
        if (launch.Kind == InteractiveAgentKind.Codex)
        {
            var home = codexHome ?? CodexPaths.LaunchHome(Environment.GetEnvironmentVariable, Environment.CurrentDirectory);
            lines.Insert(2, home is null ? "Remove-Item Env:CODEX_HOME -ErrorAction SilentlyContinue"
                : "$env:CODEX_HOME = " + Quote(home));
        }
        if (launch.Kind == InteractiveAgentKind.Pi && InteractiveAgentCommand.ManagedConfigPath(launch) is { } config && File.Exists(config))
        {
            lines.Insert(2, "$env:PI_MCP_CONFIG_MODE = 'exclusive'");
        }
        // PowerShell 5.1 needs a UTF-8 BOM. Joining lines explicitly preserves
        // literal newlines within a quoted prompt (no text-mode LF conversion).
        return [.. new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n"))];
    }

    const string WindowsAgentJobScript = """
        $source = @'
        using System;
        using System.Runtime.InteropServices;
        public static class AtfAgentJob {
            [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
            public static extern IntPtr CreateJobObject(IntPtr attributes, IntPtr name);
            [DllImport("kernel32.dll", SetLastError=true)]
            public static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
            [DllImport("kernel32.dll", SetLastError=true)]
            public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
            [DllImport("kernel32.dll", SetLastError=true)]
            public static extern bool CloseHandle(IntPtr handle);
        }
        '@
        $native = Add-Type -TypeDefinition $source -PassThru
        $job = $native::CreateJobObject([IntPtr]::Zero, [IntPtr]::Zero)
        if ($job -eq [IntPtr]::Zero) { throw 'agent job creation failed' }
        try {
            $size = if ([IntPtr]::Size -eq 8) { 144 } else { 112 }
            $limits = New-Object byte[] $size
            [BitConverter]::GetBytes([uint32]0x2000).CopyTo($limits, 16)
            $memory = [Runtime.InteropServices.Marshal]::AllocHGlobal($size)
            try {
                [Runtime.InteropServices.Marshal]::Copy($limits, 0, $memory, $size)
                if (-not $native::SetInformationJobObject($job, 9, $memory, [uint32]$size)) {
                    throw 'agent job limits failed'
                }
            } finally { [Runtime.InteropServices.Marshal]::FreeHGlobal($memory) }
            # A recorded start error proves the agent never ran. WT's default
            # close-on-graceful-exit policy closes the failed tab.
            try { $agent = [System.Diagnostics.Process]::Start($start) } catch {
                [System.IO.File]::WriteAllText(__START_ERROR__, $_.Exception.GetBaseException().Message)
                exit 0
            }
            if (-not $native::AssignProcessToJobObject($job, $agent.Handle) -and -not $agent.HasExited) {
                $agent.Kill()
                throw 'agent job assignment failed'
            }
            try {
                ($agent.Id.ToString() + '|' + $agent.StartTime.ToUniversalTime().Ticks) | Out-File -FilePath __AGENT_SIDECAR__ -Encoding ascii
            } catch { }
            $agent.WaitForExit()
        } finally { [void]$native::CloseHandle($job) }
        """;

    internal static IReadOnlyList<string> AgentArguments(InteractiveLaunch launch, string prompt) =>
        AgentArguments(launch, prompt, OperatingSystem.IsWindows());

    internal static IReadOnlyList<string> AgentArguments(InteractiveLaunch launch, string prompt, bool windowsCommandLine)
    {
        var executable = launch.Kind switch
        {
            InteractiveAgentKind.Claude => [WindowsAgentBinary("claude")],
            InteractiveAgentKind.Codex => [WindowsAgentBinary("codex")],
            InteractiveAgentKind.Pi => WindowsPiLauncher(),
            _ => throw new ArgumentOutOfRangeException(nameof(launch)),
        };
        var args = new List<string>(executable);
        EnsureInteractiveCodexNative(launch.Kind, args[0]);
        args.AddRange(InteractiveAgentCommand.ManagedArguments(launch, piShortApprove: true));
        if (launch.Kind == InteractiveAgentKind.Codex && OperatingSystem.IsWindows())
        {
            // Hooks only feed the Windows tab state marker; elsewhere they would replace user hooks.
            // Keep them before a trailing `resume <id>` subcommand.
            args.InsertRange(launch.ResumeSessionId is null ? args.Count : args.Count - 2, CodexHookArguments(HookLauncher(launch)));
        }
        if (launch.Kind == InteractiveAgentKind.Claude)
        {
            args.Add("--");
        }
        var command = args[0];
        if (command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            args.Add(ShimPrompt(launch.Kind, Path.ChangeExtension(launch.BootstrapPath, ".prompt.txt"), prompt));
            WindowsCliLaunch.EnsureCmdSafe(args);
            return args;
        }
        args.Add(launch.Kind == InteractiveAgentKind.Pi && prompt.Length > 0 && prompt[0] is '@' or '/' or '-'
            || launch.Kind == InteractiveAgentKind.Codex && prompt.StartsWith('-', StringComparison.Ordinal)
            ? "\n" + prompt : prompt);
        if (windowsCommandLine && CommandLine(args).Length > MaxWindowsCommandLineChars)
        {
            // CreateProcess caps the whole command line at 32 Ki chars; hand a long prompt over in its private file.
            args[^1] = ShimPrompt(launch.Kind, Path.ChangeExtension(launch.BootstrapPath, ".prompt.txt"), prompt);
        }
        return args;
    }

    internal const int MaxWindowsCommandLineChars = 32_000;

    internal static void EnsureInteractiveCodexNative(InteractiveAgentKind kind, string binary)
    {
        if (kind == InteractiveAgentKind.Codex && binary.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            throw new BackendNotStartedException("interactive Codex requires native codex.exe; install the native Codex CLI instead of the .cmd shim");
        }
    }

    internal static void ValidateWindowsAgentBinary(string binary)
    {
        try
        {
            using var stream = File.OpenRead(binary);
            if (binary.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) && stream.Length > 0) { return; }
            if (!binary.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new BackendNotStartedException($"agent launcher is not an executable or command script: {binary}");
            }
            using var pe = new PEReader(stream);
            if (pe.PEHeaders.PEHeader is null ||
                !pe.PEHeaders.CoffHeader.Characteristics.HasFlag(Characteristics.ExecutableImage))
            {
                throw new BadImageFormatException("missing executable PE header");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            throw new BackendNotStartedException($"agent launcher is missing or invalid: {binary}", ex);
        }
    }

    static string Quote(string value) => PowerShellText.Quote(value);

    /// <summary>Joins arguments so CommandLineToArgvW and the MSVC runtime split them back unchanged.</summary>
    internal static string CommandLine(IEnumerable<string> args) => string.Join(' ', args.Select(arg =>
    {
        if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '\n' or '\r' or '\v' or '"'))
        {
            return arg;
        }

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            // Backslashes are literal unless they precede a quote.
            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }));

    /// <summary>Points the agent at the prompt file. Pi inlines @file content; others need the correlation marker inline to bind the turn.</summary>
    internal static string ShimPrompt(InteractiveAgentKind kind, string path, string prompt = "")
    {
        if (kind == InteractiveAgentKind.Pi) { return "@" + path; }
        var marker = prompt.LastIndexOf("[AgentTeamForge correlation id: ", StringComparison.Ordinal);
        return "Read the complete task in this UTF-8 file and follow it: " + path + (marker < 0 ? "" : " " + prompt[marker..].TrimEnd());
    }

    internal static IReadOnlyList<string> CodexHookArguments(string launcher)
    {
        if (launcher.Contains('\''))
        {
            throw new BackendNotStartedException("Codex hook path contains an unsupported quote");
        }
        var args = new List<string>();
        foreach (var evt in new[] { "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Stop" })
        {
            args.Add("-c");
            args.Add($"hooks.{evt}=[{{hooks=[{{type='command',command='true',commandWindows='{launcher}'}}]}}]");
        }
        args.Add("--dangerously-bypass-hook-trust");
        return args;
    }

    static string HookScript(InteractiveLaunch launch) => Path.ChangeExtension(launch.BootstrapPath, ".hook.ps1");
    static string HookLauncher(InteractiveLaunch launch) => Path.ChangeExtension(launch.BootstrapPath, ".hook.cmd");

    static string CodexHookLauncher(InteractiveLaunch launch) => "@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File \""
        + HookScript(launch) + "\"\r\nexit /b %errorlevel%\r\n";

    static string CodexHookScript(InteractiveLaunch launch)
    {
        var marker = Path.ChangeExtension(launch.BootstrapPath, ".state.json");
        return "$ErrorActionPreference = 'Stop'\r\n"
            + "$eventJson = [Console]::In.ReadToEnd() | ConvertFrom-Json\r\n"
            + "$state = if ($eventJson.hook_event_name -eq 'Stop') { 'waiting' } else { 'running' }\r\n"
            + "$record = @{ state = $state; event = $eventJson.hook_event_name; ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() / 1000.0 } | ConvertTo-Json -Compress\r\n"
            + "$target = " + Quote(marker) + "\r\n"
            + "$temporary = $target + '.' + $PID + '.tmp'\r\n"
            + "$record | Set-Content -LiteralPath $temporary -Encoding UTF8\r\n"
            + "Move-Item -LiteralPath $temporary -Destination $target -Force\r\n";
    }


    internal static string WindowsAgentBinary(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return name;
        }

        if (FindExecutable(name + ".exe") is { } direct)
        {
            return direct;
        }

        var shim = FindExecutable(name + ".cmd")
            ?? throw new BackendNotStartedException($"{name} is unavailable");

        var root = Path.GetDirectoryName(shim)!;
        if (name == "claude")
        {
            var native = Path.Combine(root, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
            if (File.Exists(native))
            {
                return native;
            }
        }
        if (name == "codex")
        {
            var (arch, triple) = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => ("x64", "x86_64-pc-windows-msvc"),
                Architecture.Arm64 => ("arm64", "aarch64-pc-windows-msvc"),
                _ => ("", "")
            };
            if (arch.Length > 0)
            {
                var package = Path.Combine(root, "node_modules", "@openai", "codex");
                var platform = "codex-win32-" + arch;
                foreach (var basePath in new[] { Path.Combine(package, "node_modules", "@openai", platform),
                    Path.Combine(root, "node_modules", "@openai", platform), package })
                {
                    foreach (var subdir in new[] { "bin", "codex" })
                    {
                        var native = Path.Combine(basePath, "vendor", triple, subdir, "codex.exe");
                        if (File.Exists(native))
                        {
                            return native;
                        }
                    }
                }
            }
        }
        return shim;
    }

    internal static IReadOnlyList<string> WindowsPiLauncher()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ["pi"];
        }

        if (FindExecutable("pi.exe") is { } direct)
        {
            return [direct];
        }

        if (FindExecutable("pi.cmd") is { } shim)
        {
            var entry = Path.Combine(Path.GetDirectoryName(shim)!, "node_modules", "@earendil-works", "pi-coding-agent", "dist", "cli.js");
            if (File.Exists(entry) && FindExecutable("node.exe") is { } node)
            {
                return [node, entry];
            }
            return [shim];
        }
        throw new BackendNotStartedException("native Pi launcher is unavailable");
    }

    internal static OwnedTab? TryReadOwned(string path, string wrapper)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.LinkTarget is not null || file.Length > 128)
            {
                return null;
            }
            var parts = File.ReadAllText(path).Trim().Split('|');
            return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && ticks > 0 && ticks <= DateTime.MaxValue.Ticks
                ? new OwnedTab(pid, new DateTime(ticks, DateTimeKind.Utc), wrapper, path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    static DateTime? TryIdentity(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    static void WaitForExit(OwnedTab tab)
    {
        for (var i = 0; i < 50 && TryIdentity(tab.Pid) == tab.Created; i++)
        {
            Thread.Sleep(100);
        }
    }

    // Terminates only this process (never a tree), and only while its start time still matches.
    static void KillIfSame(int pid, DateTime created)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var started = process.StartTime.ToUniversalTime();
            if (started == created)
            {
                process.Kill();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
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

    internal sealed record OwnedTab(int Pid, DateTime Created, string Wrapper, string Sidecar);
}
