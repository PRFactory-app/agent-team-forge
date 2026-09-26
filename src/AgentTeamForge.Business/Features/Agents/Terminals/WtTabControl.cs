using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>Windows Terminal hands off the tab; the wrapper PID and creation time own its lifetime.</summary>
internal sealed class WtTabControl : IWtTabControl
{
    readonly ConcurrentDictionary<string, OwnedTab> _tabs = [];

    public void Preflight(InteractiveAgentKind kind)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new BackendNotStartedException("wt interactive mode requires Windows");
        }
        if (FindExecutable("wt.exe") is null)
        {
            throw new BackendNotStartedException("Windows Terminal (wt.exe) is unavailable");
        }
        if (FindExecutable("powershell.exe") is null)
        {
            throw new BackendNotStartedException("Windows PowerShell is unavailable");
        }
        if (kind == InteractiveAgentKind.Pi)
        {
            _ = WindowsPiLauncher();
        }
        else
        {
            _ = WindowsAgentBinary(kind == InteractiveAgentKind.Claude ? "claude" : "codex");
        }
    }

    public async Task StartAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken)
    {
        Preflight(launch.Kind);
        var wrapper = launch.BootstrapPath;
        var sidecar = Path.ChangeExtension(wrapper, ".pid");
        Directory.CreateDirectory(Path.GetDirectoryName(wrapper)!);
        if (File.Exists(sidecar))
        {
            File.Delete(sidecar);
        }

        await File.WriteAllBytesAsync(wrapper, WrapperBytes(launch, prompt, sidecar), cancellationToken);

        var start = new ProcessStartInfo("wt.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-w", "wt-atf", "nt", "--title", launch.AgentName,
            "--suppressApplicationTitle", "--", "powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", wrapper })
        {
            start.ArgumentList.Add(arg);
        }
        using var launcher = Process.Start(start) ?? throw new IOException("wt.exe did not start");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        while (true)
        {
            if (TryReadPid(sidecar) is { } pid && TryIdentity(pid) is { } created)
            {
                // The wt.exe PID is transient. Only the in-tab wrapper is an owned handle.
                _tabs[launch.AgentName] = new OwnedTab(pid, created, wrapper, sidecar);
                await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
                if (!IsAlive(launch))
                {
                    _tabs.TryRemove(launch.AgentName, out _);
                    throw new IOException("Windows Terminal tab exited during startup");
                }
                return;
            }
            if (launcher.HasExited && launcher.ExitCode != 0)
            {
                throw new IOException($"wt.exe exited {launcher.ExitCode}");
            }
            await Task.Delay(100, deadline.Token);
        }
    }

    public int? ProcessId(InteractiveLaunch launch) =>
        _tabs.TryGetValue(launch.AgentName, out var tab) ? tab.Pid : null;

    public bool IsAlive(InteractiveLaunch launch) =>
        _tabs.TryGetValue(launch.AgentName, out var tab) && TryIdentity(tab.Pid) == tab.Created;

    public void StopOwned(InteractiveLaunch launch)
    {
        if (!_tabs.TryGetValue(launch.AgentName, out var tab))
        {
            return;
        }

        if (TryIdentity(tab.Pid) == tab.Created)
        {
            // Killing the agent child lets the wrapper reach exit 0, which closes its tab.
            // Windows never reparents, so a process whose dead parent had this PID also
            // reports it as ParentProcessId; only children started after the wrapper are ours.
            foreach (var child in DirectChildren(tab.Pid))
            {
                if (IsOwnedChild(TryIdentity(child), tab.Created))
                {
                    KillTree(child);
                }
            }
            for (var i = 0; i < 50 && TryIdentity(tab.Pid) == tab.Created; i++)
            {
                Thread.Sleep(100);
            }

            if (TryIdentity(tab.Pid) == tab.Created)
            {
                KillTree(tab.Pid);
            }
        }
        _tabs.TryRemove(launch.AgentName, out _);
        try { File.Delete(tab.Sidecar); } catch (IOException) { }
        try { File.Delete(tab.Wrapper); } catch (IOException) { }
    }

    internal static byte[] WrapperBytes(InteractiveLaunch launch, string prompt, string sidecar)
    {
        var args = AgentArguments(launch, prompt);
        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            // A tab attached to an existing WT window inherits that window's environment.
            // Remove the caller's agent/session identity before starting a child agent.
            "Get-ChildItem Env: | Where-Object { $_.Name -match '^(CLAUDE_CODE_|CLAUDE_TEAMS_|WIN_AGENT_TEAMS_|AGENT_)' -or $_.Name -in @('CLAUDECODE','CLAUDE_PID','CODEX_THREAD_ID') } | ForEach-Object { Remove-Item -LiteralPath ('Env:' + $_.Name) }",
            "Set-Location -LiteralPath " + Quote(launch.WorkingDirectory),
            "$PID | Out-File -FilePath " + Quote(sidecar) + " -Encoding ascii",
            // Windows PowerShell 5.1 does not escape embedded double quotes when it
            // passes arguments to a native program, which splits or rewrites the
            // prompt. Start the agent with a pre-built command line instead.
            "$start = New-Object System.Diagnostics.ProcessStartInfo",
            "$start.FileName = " + Quote(args[0]),
            "$start.Arguments = " + Quote(CommandLine(args.Skip(1))),
            "$start.WorkingDirectory = " + Quote(launch.WorkingDirectory),
            "$start.UseShellExecute = $false",
            "$agent = [System.Diagnostics.Process]::Start($start)",
            "$agent.WaitForExit()",
            "exit 0"
        };
        // PowerShell 5.1 needs a UTF-8 BOM. Joining lines explicitly preserves
        // literal newlines within a quoted prompt (no text-mode LF conversion).
        return [.. new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n"))];
    }

    internal static IReadOnlyList<string> AgentArguments(InteractiveLaunch launch, string prompt)
    {
        var args = new List<string>();
        switch (launch.Kind)
        {
            case InteractiveAgentKind.Claude:
                args.AddRange([WindowsAgentBinary("claude"), "--permission-mode", "bypassPermissions"]);
                if (launch.ResumeSessionId is { } claudeId)
                {
                    args.AddRange(["--resume", claudeId]);
                }
                args.Add("--");

                break;
            case InteractiveAgentKind.Codex:
                args.AddRange([WindowsAgentBinary("codex"), "--dangerously-bypass-approvals-and-sandbox", "-C", launch.WorkingDirectory]);
                if (launch.ResumeSessionId is { } codexId)
                {
                    args.AddRange(["resume", codexId]);
                }

                break;
            case InteractiveAgentKind.Pi:
                args.AddRange(WindowsPiLauncher());
                args.AddRange(["-a", "--session-dir", launch.PiSessionDirectory!,
                    "--exclude-tools", "ask_user,ask_question,ask_human,request_input"]);
                if (launch.ResumeSessionId is not null)
                {
                    args.Add("--continue");
                }

                break;
            default: throw new ArgumentOutOfRangeException(nameof(launch));
        }
        args.Add(launch.Kind == InteractiveAgentKind.Pi && prompt.Length > 0 && prompt[0] is '@' or '/' or '-'
            || launch.Kind == InteractiveAgentKind.Codex && prompt.StartsWith('-', StringComparison.Ordinal)
            ? "\n" + prompt : prompt);
        return args;
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
        // A .cmd shim sends multiline prompts through cmd.exe and truncates them.
        throw new BackendNotStartedException($"native {name} executable is unavailable");
    }

    static IReadOnlyList<string> WindowsPiLauncher()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ["pi"];
        }

        if (FindExecutable("pi.exe") is { } direct)
        {
            return [direct];
        }

        if (FindExecutable("pi.cmd") is { } shim && FindExecutable("node.exe") is { } node)
        {
            var entry = Path.Combine(Path.GetDirectoryName(shim)!, "node_modules", "@earendil-works", "pi-coding-agent", "dist", "cli.js");
            if (File.Exists(entry))
            {
                return [node, entry];
            }
        }
        throw new BackendNotStartedException("native Pi launcher is unavailable");
    }

    static int? TryReadPid(string path)
    {
        try
        {
            return int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0 ? pid : null;
        }
        catch (IOException) { return null; }
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

    internal static bool IsOwnedChild(DateTime? childStarted, DateTime wrapperStarted) =>
        childStarted is { } started && started >= wrapperStarted;

    static int[] DirectChildren(int pid)
    {
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add($"Get-CimInstance Win32_Process -Filter 'ParentProcessId={pid}' | Select-Object -ExpandProperty ProcessId");
        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var child) ? child : 0)
                .Where(child => child > 0)];
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { return []; }
    }

    static void KillTree(int pid)
    {
        var info = new ProcessStartInfo("taskkill.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/PID", pid.ToString(CultureInfo.InvariantCulture), "/T", "/F" })
        {
            info.ArgumentList.Add(arg);
        }

        try { using var process = Process.Start(info); process?.WaitForExit(5000); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { }
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

    sealed record OwnedTab(int Pid, DateTime Created, string Wrapper, string Sidecar);
}
