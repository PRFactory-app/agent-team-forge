using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Tests.Support;
using System.Text.Json;

namespace AgentTeamForge.Tests.Features.Setup;

public sealed class SetupCommandTests
{
    [Fact]
    public void WindowsDrivePathIsALocalPiExtensionSource()
    {
        Assert.True(ClientSetup.IsLocalPackageSource(@"C:\Program Files\AgentTeamForge\extensions\pi-wake"));
        Assert.False(ClientSetup.IsLocalPackageSource("npm:pi-mcp-adapter"));
    }

    [Fact]
    public void StableBinaryFollowsCurrentReleaseLink()
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var release = Path.Combine(home, ".local", "share", "agentteamforge", "releases", "v1");
        var current = Path.Combine(home, ".local", "share", "agentteamforge", "current");
        var stable = Path.Combine(home, ".local", "bin", "atf");
        Directory.CreateDirectory(release);
        Directory.CreateDirectory(Path.GetDirectoryName(stable)!);
        File.WriteAllText(Path.Combine(release, "atf"), "binary");
        Directory.CreateSymbolicLink(current, "releases/v1");
        File.CreateSymbolicLink(stable, "../share/agentteamforge/current/atf");

        Assert.Equal(stable, ClientSetup.StableBinary(Path.Combine(release, "atf"), home));
    }

    [Fact]
    public void SetupRequiresExplicitMode()
    {
        using var temp = new TempStateDir();
        var options = new Dictionary<string, string> { ["state-dir"] = temp.File("state") };

        Assert.Equal(64, SetupCommand.Run(options, (_, _) => throw new InvalidOperationException()));
        Assert.False(Directory.Exists(options["state-dir"]));
    }

    [Fact]
    public void WtModeIsRejectedOnLinuxBeforeCreatingState()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempStateDir();
        var dir = temp.File("state");
        Assert.Equal(64, SetupCommand.Run(new Dictionary<string, string> { ["mode"] = "wt", ["state-dir"] = dir }));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void MacTerminalModeIsRejectedOffMacBeforeCreatingState()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        using var temp = new TempStateDir();
        var dir = temp.File("state");
        Assert.Equal(64, SetupCommand.Run(new Dictionary<string, string> { ["mode"] = "terminal", ["state-dir"] = dir }));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void ApplyReconcilesAndPreservesOtherEntries()
    {
        using var temp = new TempStateDir();
        var options = new Dictionary<string, string>
        {
            ["mode"] = "headless",
            ["state-dir"] = temp.File("state"),
            ["apply"] = "true",
        };
        var home = temp.File("home");
        var extension = temp.File("pi-wake");
        Directory.CreateDirectory(extension);
        File.WriteAllText(Path.Combine(extension, "package.json"), "{}");
        var registered = new Dictionary<string, (string Binary, string State)>
        {
            ["claude"] = ("/old/atf", "/old/state"),
            ["codex"] = ("/old/atf", "/old/state"),
        };
        var claudeSettings = Path.Combine(home, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(claudeSettings)!);
        File.WriteAllText(claudeSettings, """{"theme":"dark","crossSessionInbound":"refuse"}""");
        var piDir = Path.Combine(home, ".pi", "agent");
        Directory.CreateDirectory(piDir);
        File.WriteAllText(Path.Combine(piDir, "mcp.json"), """{"other":true,"mcpServers":{"other":{"command":"other"}}}""");
        File.WriteAllText(Path.Combine(piDir, "settings.json"), """{"theme":"dark","packages":[{"source":"npm:other","extensions":[]}]}""");
        var adds = 0;
        (int, string) Runner(string tool, IReadOnlyList<string> args)
        {
            if (args[0] == "--version")
            {
                return (0, "test");
            }
            if (tool == "pi")
            {
                Assert.Equal("install", args[0]);
                var settings = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(piDir, "settings.json")))!.AsObject();
                settings["packages"]!.AsArray().Add((System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(args[1]));
                File.WriteAllText(Path.Combine(piDir, "settings.json"), settings.ToJsonString());
                return (0, "");
            }
            if (args[1] == "get")
            {
                if (!registered.TryGetValue(tool, out var value))
                {
                    return (1, "");
                }
                return (0, $"Scope: User config\n  enabled: true\n  Command: {value.Binary}\n  Args: mcp --state-dir {value.State}\n");
            }
            if (args[1] == "remove")
            {
                registered.Remove(tool);
                return (0, "");
            }
            Assert.Equal("add", args[1]);
            Assert.Equal("agentteamforge", args.Contains("--scope") ? args[4] : args[2]);
            var delimiter = Array.IndexOf([.. args], "--");
            registered[tool] = (args[delimiter + 1], args[delimiter + 4]);
            adds++;
            return (0, "");
        }

        Assert.Equal(0, SetupCommand.Run(options, Runner, "/tmp/atf", claudeSettings, home, extension));
        var firstSettings = File.ReadAllText(claudeSettings);
        var firstPiMcp = File.ReadAllText(Path.Combine(piDir, "mcp.json"));
        var firstPiSettings = File.ReadAllText(Path.Combine(piDir, "settings.json"));
        Assert.Equal(0, SetupCommand.Run(options, Runner, "/tmp/atf", claudeSettings, home, extension));
        Assert.Equal(2, adds);
        Assert.Contains("headless", File.ReadAllText(Path.Combine(options["state-dir"], "launch-mode.json")));
        Assert.Equal(firstSettings, File.ReadAllText(claudeSettings));
        using var settings = JsonDocument.Parse(firstSettings);
        Assert.Equal("dark", settings.RootElement.GetProperty("theme").GetString());
        Assert.Equal("accept", settings.RootElement.GetProperty("crossSessionInbound").GetString());
        Assert.Equal(firstPiMcp, File.ReadAllText(Path.Combine(piDir, "mcp.json")));
        Assert.Equal(firstPiSettings, File.ReadAllText(Path.Combine(piDir, "settings.json")));
        using var piMcp = JsonDocument.Parse(firstPiMcp);
        Assert.True(piMcp.RootElement.GetProperty("other").GetBoolean());
        Assert.Equal("other", piMcp.RootElement.GetProperty("mcpServers").GetProperty("other").GetProperty("command").GetString());
        using var piSettings = JsonDocument.Parse(firstPiSettings);
        Assert.Equal("dark", piSettings.RootElement.GetProperty("theme").GetString());
        Assert.Contains(piSettings.RootElement.GetProperty("packages").EnumerateArray(), value => value.ValueKind == JsonValueKind.Object && value.GetProperty("source").GetString() == "npm:other");
        using var piState = JsonDocument.Parse(File.ReadAllText(Path.Combine(piDir, "agentteamforge.json")));
        Assert.Equal(options["state-dir"], piState.RootElement.GetProperty("stateDir").GetString());
    }

    [Fact]
    public void CheckReportsMissingWithoutWriting()
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var extension = temp.File("pi-wake");
        var options = new Dictionary<string, string> { ["check"] = "true", ["state-dir"] = temp.File("state") };
        static (int, string) Runner(string tool, IReadOnlyList<string> args) => args[0] == "--version" ? (0, "test") : (1, "");

        Assert.Equal(1, SetupCommand.Run(options, Runner, "/tmp/atf", homePath: home, extensionPath: extension));
        Assert.False(Directory.Exists(home));
        Assert.False(Directory.Exists(options["state-dir"]));
    }

    [Fact]
    public async Task StartDetectsOwnedRunningDaemon()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var daemon = await rig.StartDaemonAsync();
        Assert.Equal(0, await SetupCommand.StartAsync(new Dictionary<string, string> { ["state-dir"] = rig.StateDir }, "/missing/atf", quiet: true));
        Assert.False(daemon.HasExited);
    }

    [Fact]
    public async Task HerdrModePersistsAndAllowsDaemonStart()
    {
        using var temp = new TempStateDir();
        var dir = temp.File("state");
        Assert.Equal(0, SetupCommand.Run(new Dictionary<string, string> { ["mode"] = "herdr", ["state-dir"] = dir },
            (_, _) => throw new InvalidOperationException(), "/tmp/atf"));

        var state = StateDirectory.Open(dir);
        Assert.Equal("herdr", SetupCommand.ConfiguredMode(state));
        Assert.False(File.Exists(state.Socket));
    }

    [Fact]
    public void SetupRejectsFakeOnlyProfile()
    {
        using var temp = new TempStateDir();
        var dir = temp.File("state");
        Assert.Equal(0, InitCommand.Run(dir, testProfile: true, queueLimit: null, maxRuntimeSeconds: null));

        Assert.Equal(78, SetupCommand.Run(new Dictionary<string, string> { ["mode"] = "headless", ["state-dir"] = dir },
            (_, _) => throw new InvalidOperationException(), "/tmp/atf"));
        Assert.False(File.Exists(Path.Combine(dir, "launch-mode.json")));
    }

    [Fact]
    public async Task StopTerminatesTheOwnedDaemonAndIsIdempotent()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var daemon = await rig.StartDaemonAsync();

        var (firstExit, firstOutput, firstError) = await rig.RunToExitAsync(["stop", "--state-dir", rig.StateDir + "/"]);
        Assert.Equal(0, firstExit);
        Assert.Empty(firstError);
        Assert.Contains($"Stopped daemon {daemon.Id}.", firstOutput);
        await Bounded.Until(() => daemon.HasExited, "stopped daemon exit");

        var (secondExit, secondOutput, secondError) = await rig.RunToExitAsync(["stop", "--state-dir", rig.StateDir]);
        Assert.Equal(0, secondExit);
        Assert.Empty(secondError);
        Assert.Contains("Daemon is not running.", secondOutput);
    }

    [Fact]
    public async Task ConcurrentBridgesStartOneDaemonThatSurvivesBridgeExit()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        try
        {
            var bridges = await Task.WhenAll(rig.StartBridgeAsync("lead-one"), rig.StartBridgeAsync("lead-two"));
            var state = StateDirectory.Open(rig.StateDir);
            var pid = DaemonLock.ReadOwnerPid(state.LockFile);
            Assert.True(pid > 0);
            Assert.Single(File.ReadLines(Path.Combine(rig.StateDir, "daemon.log")),
                line => line.StartsWith("[atf-daemon] ready pid=", StringComparison.Ordinal));
            Assert.All(bridges, bridge => Assert.False(bridge.Process.HasExited));

            foreach (var (process, client) in bridges)
            {
                await client.DisposeAsync();
                process.Kill();
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            }

            Assert.Equal(pid, DaemonLock.ReadOwnerPid(state.LockFile));
            Assert.True((await rig.ClientAsync("list")).Ok);
            Assert.Equal(pid, DaemonLock.ReadOwnerPid(state.LockFile));
        }
        finally
        {
            await rig.RunToExitAsync(["stop", "--state-dir", rig.StateDir]);
        }
    }

    [Fact]
    public void LoginAutostartWritesAndRemovesLinuxUnitAndMacPlistInTempHome()
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var calls = new List<string>();
        (int, string) Runner(string tool, IReadOnlyList<string> args)
        {
            calls.Add(tool + " " + string.Join(' ', args));
            return (0, "");
        }

        var binary = "/tmp/atf binary";
        var state = "/tmp/atf state";
        Assert.Equal(0, LoginAutostart.Apply(home, binary, state, true, Runner, "linux"));
        var unit = File.ReadAllText(LoginAutostart.FilePath(home, "linux"));
        Assert.Contains("ExecStart=\"/tmp/atf binary\" daemon --state-dir \"/tmp/atf state\"", unit);
        Assert.Contains("WantedBy=default.target", unit);
        Assert.Contains("Environment=\"PATH=" + Environment.GetEnvironmentVariable("PATH"), unit);
        Assert.Contains("systemctl --user enable agentteamforge.service", calls);
        Assert.Equal(0, LoginAutostart.Apply(home, binary, state, false, Runner, "linux"));
        Assert.False(LoginAutostart.IsInstalled(home, "linux"));

        Assert.Equal(0, LoginAutostart.Apply(home, binary, state, true, Runner, "macos"));
        var plist = File.ReadAllText(LoginAutostart.FilePath(home, "macos"));
        Assert.Contains("<string>/tmp/atf binary</string>", plist);
        Assert.Contains("<key>RunAtLoad</key><true/>", plist);
        Assert.Contains("<key>PATH</key>", plist);
        Assert.Equal(0, LoginAutostart.Apply(home, binary, state, false, Runner, "macos"));
        Assert.False(LoginAutostart.IsInstalled(home, "macos"));
    }

    [Fact]
    public void AutostartOnSpellingEnablesForConfiguredStateInTempHome()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temp = new TempStateDir();
        var dir = temp.File("state");
        var home = temp.File("home");
        Assert.Equal(0, SetupCommand.Run(new Dictionary<string, string> { ["mode"] = "headless", ["state-dir"] = dir },
            (_, _) => throw new InvalidOperationException(), "/tmp/atf", homePath: home));

        Assert.Equal(0, SetupCommand.Run(new Dictionary<string, string> { ["autostart"] = "on", ["apply"] = "true", ["state-dir"] = dir },
            (_, _) => (0, ""), "/tmp/atf", homePath: home));
        Assert.True(LoginAutostart.IsInstalled(home, "linux"));
    }

    [Fact]
    public void DaemonEnvironmentDropsLeadIdentityAndKeepsAgentConfiguration()
    {
        var kept = new[]
        {
            "PATH", "HOME", "HTTPS_PROXY", "XDG_RUNTIME_DIR", "SSH_AUTH_SOCK", "OPENAI_API_KEY", "ANTHROPIC_API_KEY",
            "CODEX_HOME", "CLAUDE_CONFIG_DIR", "CLAUDE_CODE_USE_BEDROCK", "GH_TOKEN", "SystemRoot",
        };
        var dropped = new[]
        {
            "CLAUDECODE", "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_ENTRYPOINT",
            "HERDR_PANE_ID", "CODEX_THREAD_ID", "AGENT_NAME", "WIN_AGENT_TEAMS_PARENT_ID", "ATF_RUN_CORRELATION",
        };
        var environment = kept.Concat(dropped).ToDictionary(key => key, key => (string?)"value");

        DaemonEnvironment.Scrub(environment);

        Assert.Equal(kept.Order(), environment.Keys.Order());
    }

    [Fact]
    public void FailedSystemdEnableRestoresUnitAndChoiceTracksInstalledFile()
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var path = LoginAutostart.FilePath(home, "linux");
        Assert.False(LoginAutostart.UseSystemdUserUnit(home, "/tmp/atf", "/tmp/state"));
        static (int, string) FailEnable(string _, IReadOnlyList<string> args) => args.Contains("enable") ? (1, "failed") : (0, "");

        Assert.Equal(1, LoginAutostart.Apply(home, "/tmp/atf", "/tmp/state", true, FailEnable, "linux"));
        Assert.False(File.Exists(path));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "old unit");
        Assert.Equal(1, LoginAutostart.Apply(home, "/tmp/atf", "/tmp/state", true, FailEnable, "linux"));
        Assert.Equal("old unit", File.ReadAllText(path));

        File.WriteAllText(path, LoginAutostart.LinuxUnit("/tmp/atf", "/tmp/state", "/usr/bin"));
        Assert.True(LoginAutostart.UseSystemdUserUnit(home, "/tmp/atf", "/tmp/state"));
        Assert.False(LoginAutostart.UseSystemdUserUnit(home, "/tmp/other/atf", "/tmp/state"));
        Assert.False(LoginAutostart.UseSystemdUserUnit(home, "/tmp/atf", "/tmp/other-state"));
    }
}
