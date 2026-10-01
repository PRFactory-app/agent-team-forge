using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Host.Features.Setup;

namespace AgentTeamForge.Tests.Features.Agents.Terminals;

public sealed class MacTabControlTests
{
    [Fact]
    public void ProcessIdentitySeparatesUnreadableLivePidFromReusedAndDeadPid()
    {
        Assert.Equal(MacTabControl.IdentityState.Unverified, MacTabControl.Identity(11, null, true));
        Assert.Equal(MacTabControl.IdentityState.Different, MacTabControl.Identity(11, 22, true));
        Assert.Equal(MacTabControl.IdentityState.Ours, MacTabControl.Identity(11, 11, true));
        Assert.Equal(MacTabControl.IdentityState.Gone, MacTabControl.Identity(11, null, false));
    }

    [Fact]
    public void ShellQuotingRoundTripsQuotesBackslashesAndUnicode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        const string value = "it's \\ a \"quote\" and ‘smart’ \u201Ddouble\u201D\nsecond line";
        var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("printf '%s' " + MacTabControl.ShellQuote(value));
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(value, output);
    }

    [Fact]
    public void WrapperKeepsSpecialCharactersAsData()
    {
        const string value = "a\\b\"c ‘quote’ \u201Dquote\u201D";
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var cwd = Path.Combine(state.Path, "a'b\\c");
        Directory.CreateDirectory(cwd);
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", cwd, null, null, "/tmp/atf.launch.sh");
        var wrapper = MacTabControl.WrapperText(launch, value + "'; echo unsafe", "/tmp/atf.pid", "/tmp/atf'binary");
        Assert.Contains("cd " + MacTabControl.ShellQuote(cwd), wrapper);
        Assert.Contains("'/tmp/atf'\"'\"'binary' terminal-token --pid \"$$\"", wrapper);
        Assert.Contains(MacTabControl.ShellQuote(value + "'; echo unsafe"), wrapper);
        Assert.Contains("if [ ! -x ", wrapper);
        Assert.Contains("/tmp/atf.start-error", wrapper);
        Assert.Contains("exec ", wrapper);
        // A syntax error would break every launch.
        if (OperatingSystem.IsWindows()) { return; }
        var script = Path.Combine(state.Path, "wrapper.sh");
        File.WriteAllText(script, wrapper);
        var info = new ProcessStartInfo("bash") { RedirectStandardError = true };
        info.ArgumentList.Add("-n");
        info.ArgumentList.Add(script);
        using var check = Process.Start(info)!;
        var errors = check.StandardError.ReadToEnd();
        Assert.True(check.WaitForExit(5000));
        Assert.True(check.ExitCode == 0, errors);
    }

    [Fact]
    public void WrapperQuotesResolvedArgumentsAndResume()
    {
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", Path.GetTempPath(), "native-1", null, "/tmp/atf.launch.sh")
            .WithSelection("model=gpt-6-sol;effort=xhigh");
        var wrapper = MacTabControl.WrapperText(launch, "task", "/tmp/atf.pid", "/tmp/atf", "/tmp/daemon-codex");
        Assert.Contains("'-m' 'gpt-6-sol' '-c' 'model_reasoning_effort=\"xhigh\"' 'resume' 'native-1'", wrapper);
        Assert.Contains("export CODEX_HOME='/tmp/daemon-codex'", wrapper);
    }

    [Fact]
    public void ClaudeTerminalWrapperExportsPerLaunchWorkspaceTrust()
    {
        var launch = new InteractiveLaunch(InteractiveAgentKind.Claude, "atftest", "/tmp/new dir", null, null, "/tmp/atf.launch.sh");
        var wrapper = MacTabControl.WrapperText(launch, "task", "/tmp/atf.pid", "/tmp/atf");

        Assert.Contains("export CLAUDE_CODE_SANDBOXED='1'", wrapper);
        Assert.Contains("unset CLAUDECODE", wrapper);
        Assert.Contains("CLAUDE_CODE_SESSION_ID", wrapper);
        Assert.Contains("CLAUDE_CODE_MESSAGING_", wrapper);
        Assert.DoesNotContain("CLAUDE_CODE_GIT_BASH_PATH", wrapper);
        Assert.Contains("'--settings' '{\"skipDangerousModePermissionPrompt\":true}'", wrapper);
    }

    [Theory]
    [InlineData(InteractiveAgentKind.Claude, "'--resume' 'native-1'")]
    [InlineData(InteractiveAgentKind.Codex, "'resume' 'native-1'")]
    [InlineData(InteractiveAgentKind.Pi, "'--continue'")]
    public void ManagedMacWrapperCarriesPrivateMcpConfigOnResume(InteractiveAgentKind kind, string resume)
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var root = Path.Combine(state.Path, "state with spaces");
        var configPath = ManagedChildContext.ConfigPath(root, "job-follow");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ManagedChildContext.ServerName] = new JsonObject
                {
                    ["command"] = "/private/atf",
                    ["args"] = new JsonArray("mcp", "--state-dir", root),
                },
            },
        }.ToJsonString());
        var launch = new InteractiveLaunch(kind, "atftest", root, "native-1", Path.Combine(root, "pi"),
            Path.Combine(root, "terminal", "follow.launch.sh"))
        { JobId = "job-follow" };

        var wrapper = MacTabControl.WrapperText(launch, "task", Path.Combine(root, "terminal", "tab.pid"), "/private/atf");
        Assert.Contains(resume, wrapper);
        if (kind == InteractiveAgentKind.Codex)
        {
            Assert.Contains("'mcp_servers.agentteamforge.command=\"/private/atf\"'", wrapper);
            Assert.Contains(MacTabControl.ShellQuote("mcp_servers.agentteamforge.args=[\"mcp\",\"--state-dir\",\"" + root + "\"]"), wrapper);
        }
        else { Assert.Contains(MacTabControl.ShellQuote(configPath), wrapper); }
        if (kind == InteractiveAgentKind.Pi) { Assert.Contains("export PI_MCP_CONFIG_MODE=exclusive", wrapper); }
    }

    [Fact]
    public void UnixCodexLaunchKeepsUserHooks()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", Path.GetTempPath(), null, null, "/tmp/atf.launch.sh");
        var args = WtTabControl.AgentArguments(launch, "task");
        Assert.DoesNotContain("--dangerously-bypass-hook-trust", args);
        Assert.DoesNotContain(args, arg => arg.StartsWith("hooks.", StringComparison.Ordinal));
    }

    [Fact]
    public void LaunchersUseArgumentListsAndDoNotEmbedPromptInAppleScript()
    {
        const string wrapper = "/tmp/a'\"‘\u201D $(x).sh";
        var terminal = MacTabControl.LaunchInfo("terminal", null, null, wrapper, "atftest");
        Assert.Equal("/usr/bin/osascript", terminal.FileName);
        Assert.Equal(["-e", "on run argv", "-e", "tell application \"Terminal\" to do script (item 1 of argv)", "-e", "end run",
            "exec /bin/sh " + MacTabControl.ShellQuote(wrapper)], terminal.ArgumentList);
        Assert.Throws<BackendNotStartedException>(() => MacTabControl.LaunchInfo("terminal", null, null, "/tmp/a\\b.sh", "atftest"));
        Assert.Throws<BackendNotStartedException>(() => MacTabControl.LaunchInfo("terminal", null, null, "/tmp/a\nb.sh", "atftest"));

        var kitty = MacTabControl.LaunchInfo("kitty", "unix:/tmp/kitty.sock", "/usr/bin/kitty", "/tmp/run.sh", "atftest");
        Assert.Equal("/usr/bin/kitty", kitty.FileName);
        Assert.Equal(["@", "--to", "unix:/tmp/kitty.sock", "launch", "--type=tab", "--tab-title", "atftest", "/bin/sh", "/tmp/run.sh"],
            kitty.ArgumentList);
        // osascript prints the tab reference and kitty the window id; inherited stdout is the daemon log.
        Assert.True(terminal.RedirectStandardOutput);
        Assert.True(kitty.RedirectStandardOutput);
    }

    [Fact]
    public void RefusedAppleEventIsACertainNotStartedWithRemedy()
    {
        var denied = MacTabControl.LauncherFailure(1, "43:69: execution error: Not authorized to send Apple events to Terminal. (-1743)\n");
        var started = Assert.IsType<BackendNotStartedException>(denied);
        Assert.Contains("Privacy & Security > Automation", started.Message);
        Assert.IsType<BackendNotStartedException>(MacTabControl.LauncherFailure(1, "execution error: (-1744)"));
        // Any other launcher failure may follow an opened tab, so it stays uncertain.
        Assert.IsType<IOException>(MacTabControl.LauncherFailure(1, "execution error: Terminal got an error: AppleEvent timed out. (-1712)"));
    }

    [Fact]
    public void KittyProbeWaitsForOutputThatOutlivesTheProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var kitty = state.File("kitty");
        // The child keeps stdout open after the probed process exits, as a large `kitty @ ls` reply can.
        File.WriteAllText(kitty, "#!/bin/sh\n(sleep 1; printf '[]') &\nexit 0\n");
        File.SetUnixFileMode(kitty, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.True(MacTabControl.KittyResponds(kitty, "unix:/tmp/kitty.sock"));
    }

    [Fact]
    public void FailedTerminalTokenRecordsAStartErrorAndNeverRunsTheAgent()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var atf = state.File("atf");
        File.WriteAllText(atf, "#!/bin/sh\nexit 150\n");
        File.SetUnixFileMode(atf, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null, state.File("atf.launch.sh"));
        var wrapper = MacTabControl.WrapperText(launch, "task", state.File("atf.launch.pid"), atf, dotnetRoot: "/opt/dotnet root");
        Assert.Contains("export DOTNET_ROOT='/opt/dotnet root'", wrapper);
        File.WriteAllText(launch.BootstrapPath, wrapper.Replace("exec ", "touch " + MacTabControl.ShellQuote(state.File("agent-ran")) + "; exec ", StringComparison.Ordinal));

        var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        info.ArgumentList.Add(launch.BootstrapPath);
        using var shell = Process.Start(info)!;
        Assert.True(shell.WaitForExit(5000));

        Assert.False(File.Exists(state.File("agent-ran")));
        Assert.Contains("terminal-token failed", new MacTabControl("terminal", null, null).StartFailure(launch));
    }

    [Fact]
    public void StoppingANeverStartedLaunchRemovesItsWrapper()
    {
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atftest", state.Path, null, null, state.File("atf.launch.sh"));
        File.WriteAllText(launch.BootstrapPath, "prompt");
        File.WriteAllText(state.File("atf.launch.start-error"), "failed");

        new MacTabControl("terminal", null, null).StopOwned(launch);

        Assert.False(File.Exists(launch.BootstrapPath));
        Assert.False(File.Exists(state.File("atf.launch.start-error")));
    }

    [Fact]
    public void StopKillsAnAgentThatIgnoresSigtermAndWaitsForItToBeReaped()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var launch = new InteractiveLaunch(InteractiveAgentKind.Claude, "atftest", state.Path, null, null, state.File("atf.launch.sh"));
        var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("trap '' TERM; while :; do sleep 1; done");
        using var agent = Process.Start(info)!;
        try
        {
            var sidecar = state.File("atf.launch.pid");
            File.WriteAllText(sidecar, $"{agent.Id} {DarwinProcess.CreationToken(agent.Id)}");
            File.SetUnixFileMode(sidecar, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            new MacTabControl("terminal", null, null).StopOwned(launch);

            Assert.True(agent.HasExited);
            Assert.False(File.Exists(sidecar));
        }
        finally
        {
            if (!agent.HasExited) { agent.Kill(); }
        }
    }

    [Fact]
    public void StopAlsoKillsWhatTheAgentLeftRunningInItsTree()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atf0123456789abcdef0123", state.Path, null, null, state.File("atf.launch.sh"));
        // Like a Codex tool shell: a platform /bin/sleep that survives its agent and never shows the launch marker.
        using var agent = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "sleep 300 & echo $!; wait"])
        { UseShellExecute = false, RedirectStandardOutput = true })!;
        var tool = int.Parse(agent.StandardOutput.ReadLine()!, System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            var sidecar = state.File("atf.launch.pid");
            File.WriteAllText(sidecar, $"{agent.Id} {DarwinProcess.CreationToken(agent.Id)}");
            File.SetUnixFileMode(sidecar, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            new MacTabControl("terminal", null, null).StopOwned(launch);

            Assert.True(agent.HasExited);
            Assert.True(SpinWait.SpinUntil(() => DarwinProcess.Info(tool) is not { IsZombie: false }, TimeSpan.FromSeconds(10)));
        }
        finally
        {
            if (!agent.HasExited) { agent.Kill(); }
            if (DarwinProcess.CreationToken(tool) is { } token) { DarwinProcess.SignalIfSame(tool, token, 9); }
        }
    }

    [Fact]
    public void EveryInteractiveCodexLaunchPutsTheLaunchMarkerIntoToolShells()
    {
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atf0123456789abcdef0123", Path.GetTempPath(), null, null, "/tmp/atf.launch.sh");
        const string marker = "shell_environment_policy.set.ATF_RUN_CORRELATION=\"atf0123456789abcdef0123\"";
        foreach (var args in new[]
        {
            HerdrAgentControl.AgentArguments(launch),
            WtTabControl.AgentArguments(launch, "task", windowsCommandLine: false),
            WtTabControl.AgentArguments(launch with { ResumeSessionId = "native-1" }, "task", windowsCommandLine: true),
        })
        {
            var at = args.ToList().IndexOf(marker);
            Assert.True(at > 0 && args[at - 1] == "-c", string.Join(' ', args));
        }
        Assert.Equal(["resume", "native-1"], HerdrAgentControl.AgentArguments(launch with { ResumeSessionId = "native-1" }).TakeLast(2));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Contains(MacTabControl.ShellQuote(marker), MacTabControl.WrapperText(launch, "task", "/tmp/atf.launch.pid", "/bin/atf"));
        }
        Assert.DoesNotContain(HerdrAgentControl.AgentArguments(launch with { Kind = InteractiveAgentKind.Claude }),
            arg => arg.StartsWith("shell_environment_policy", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("No models available. Use /login to log into a provider via OAuth or API key. See:\n  /opt/pi/docs/providers.md", true)]
    [InlineData("provider  model\nanthropic claude-sonnet-4-5", false)]
    public void PiLoginProbeRecordsPisOwnVerdictBesideTheTui(string listModelsOutput, bool signedOut)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        Directory.CreateDirectory(state.File("terminal"));
        var launch = new InteractiveLaunch(InteractiveAgentKind.Pi, "atftest", state.Path, null, Path.Combine(state.Path, "pi"),
            Path.Combine(state.Path, "terminal", "atftest.launch.sh"))
        { Model = "anthropic/claude-sonnet-4-5" };
        var argsFile = state.File("probe-args");
        var fakePi = state.File("pi");
        File.WriteAllText(fakePi, "#!/bin/sh\nprintf '%s\\n' \"$@\" > " + MacTabControl.ShellQuote(argsFile) + "\nprintf '%s\\n' "
            + MacTabControl.ShellQuote(listModelsOutput) + "\n");
        File.SetUnixFileMode(fakePi, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var args = WtTabControl.AgentArguments(launch, "the prompt atf-corr:x", windowsCommandLine: false);
        var probe = MacTabControl.PiLoginProbe(fakePi, args, Path.ChangeExtension(launch.BootstrapPath, ".pid"));
        Assert.Contains(probe, MacTabControl.WrapperText(launch, "the prompt atf-corr:x", Path.ChangeExtension(launch.BootstrapPath, ".pid"), "/bin/atf")
            .Replace(MacTabControl.ShellQuote(MacTabControl.FindExecutable("pi") ?? "pi"), MacTabControl.ShellQuote(fakePi), StringComparison.Ordinal));

        using (var shell = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", probe + "wait\n"]) { UseShellExecute = false })!)
        {
            Assert.True(shell.WaitForExit(10_000));
        }
        // Same model and approval as the TUI, never the prompt.
        var probeArgs = File.ReadAllLines(argsFile);
        Assert.Contains("anthropic/claude-sonnet-4-5", probeArgs);
        Assert.Equal(["--offline", "--list-models"], probeArgs[^2..]);
        Assert.DoesNotContain(probeArgs, arg => arg.Contains("atf-corr", StringComparison.Ordinal));
        var check = Path.ChangeExtension(launch.BootstrapPath, ".login-check");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(check));

        var blocker = new MacTabControl("terminal", null, null).LoginBlocker(launch);
        Assert.Equal(signedOut, blocker is not null);
        if (signedOut)
        {
            Assert.Equal("agent_login_required", blocker!.Code);
            Assert.Contains("run `pi` and /login", blocker.Details);
        }
        Assert.Null(new MacTabControl("terminal", null, null).LoginBlocker(launch with { Kind = InteractiveAgentKind.Claude }));
        Assert.DoesNotContain("--list-models", MacTabControl.WrapperText(launch with { Kind = InteractiveAgentKind.Codex },
            "p", Path.ChangeExtension(launch.BootstrapPath, ".pid"), "/bin/atf"));
    }

    [Fact]
    public void LiveAgentIsAdoptedFromItsSidecarAndRetainedAcrossRestart()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        Directory.CreateDirectory(state.File("terminal"));
        var launch = new InteractiveLaunch(InteractiveAgentKind.Pi, "atftest", state.Path, null, null, Path.Combine(state.Path, "terminal", "atftest.launch.sh"));
        File.WriteAllText(launch.BootstrapPath, "prompt");
        using var agent = Process.Start(new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false })!;
        try
        {
            var sidecar = Path.ChangeExtension(launch.BootstrapPath, ".pid");
            File.WriteAllText(sidecar, $"{agent.Id} {DarwinProcess.CreationToken(agent.Id)}");
            File.SetUnixFileMode(sidecar, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            // The wrapper reported its PID after StartAsync stopped waiting.
            var tabs = new MacTabControl("terminal", null, null);
            Assert.True(tabs.IsAlive(launch));
            Assert.Equal(agent.Id, tabs.ProcessId(launch));

            tabs.Retained(launch, "session-1");
            var (survivorSession, survivorLaunch) = Assert.Single(MacTabControl.Survivors(state.Path, InteractiveAgentKind.Pi));
            Assert.Equal("session-1", survivorSession);
            Assert.Equal("atftest", survivorLaunch.AgentName);
            Assert.Empty(MacTabControl.Survivors(state.Path, InteractiveAgentKind.Codex));

            new MacTabControl("terminal", null, null).StopOwned(survivorLaunch);
            Assert.True(agent.WaitForExit(5000));
            Assert.Empty(MacTabControl.Survivors(state.Path, InteractiveAgentKind.Pi));
            Assert.False(File.Exists(launch.BootstrapPath));
        }
        finally
        {
            if (!agent.HasExited) { agent.Kill(); }
        }
    }

    [Fact]
    public void SetupChoosesKittyOnlyWhenRemoteControlResponds()
    {
        static (int ExitCode, string Output) Ready(string tool, IReadOnlyList<string> args)
        {
            Assert.Equal("/usr/bin/kitty", tool);
            Assert.Equal(["@", "--to", "unix:/tmp/kitty.sock", "ls"], args);
            return (0, "[]");
        }

        Assert.Equal("kitty", SetupCommand.SelectMacTerminal("unix:/tmp/kitty.sock", Ready, "/usr/bin/kitty").TerminalProvider);
        Assert.Equal("terminal", SetupCommand.SelectMacTerminal("unix:/tmp/kitty.sock", (_, _) => (1, ""), "/usr/bin/kitty").TerminalProvider);
        Assert.Equal("terminal", SetupCommand.SelectMacTerminal(null, (_, _) => throw new InvalidOperationException(), "/usr/bin/kitty").TerminalProvider);
    }
}
