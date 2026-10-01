using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Tests.Support;
using System.Diagnostics;
using System.Text.Json;

namespace AgentTeamForge.Tests.Features.Setup;

public sealed class SetupCommandTests
{
    static (int, string) NoClient(string tool, IReadOnlyList<string> args) => (127, "");

    [Fact]
    public void WindowsDrivePathIsALocalPiExtensionSource()
    {
        Assert.True(ClientSetup.IsLocalPackageSource(@"C:\Program Files\AgentTeamForge\extensions\pi-wake"));
        Assert.False(ClientSetup.IsLocalPackageSource("npm:pi-mcp-adapter"));
    }

    [Fact]
    public void ClientFailureDetailIsBounded()
    {
        Assert.Equal(401, ClientSetup.BoundedError(new string('x', 500)).Length);
        Assert.Contains("network unavailable", ClientSetup.BoundedError("\nnetwork unavailable\n"));
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
    public void CurrentRelease_MapsOldReleaseImageToCurrent()
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var root = Path.Combine(home, ".local", "share", "agentteamforge");
        foreach (var version in new[] { "0.0.6", "0.0.8" })
        {
            Directory.CreateDirectory(Path.Combine(root, "releases", version));
            File.WriteAllText(Path.Combine(root, "releases", version, "atf"), "binary");
        }
        var old = Path.Combine(root, "releases", "0.0.6", "atf");
        var outside = temp.File("elsewhere/atf");

        Assert.Equal(old, ClientSetup.CurrentRelease(old, home));

        Directory.CreateSymbolicLink(Path.Combine(root, "current"), "releases/0.0.8");

        Assert.Equal(Path.Combine(root, "releases", "0.0.8", "atf"), ClientSetup.CurrentRelease(old, home));
        Assert.Equal(outside, ClientSetup.CurrentRelease(outside, home));
    }

    [Fact]
    public void SetupRequiresExplicitMode()
    {
        using var temp = new TempStateDir();
        var options = new Dictionary<string, string> { ["state-dir"] = temp.File("state") };

        Assert.Equal(64, SetupCommand.Run(options, NoClient, input: new StringReader(""), interactive: false));
        Assert.False(Directory.Exists(options["state-dir"]));
    }

    [Fact]
    public void InteractiveChoicePersistsAndBareRerunPreservesMode()
    {
        using var temp = new TempStateDir();
        var dir = temp.File("state");
        var options = new Dictionary<string, string> { ["state-dir"] = dir, ["force"] = "true" };
        Assert.Equal(0, SetupCommand.Run(options, NoClient, "/tmp/atf", homePath: temp.File("home"),
            input: new StringReader("headless\n"), interactive: true));
        Assert.Equal("headless", SetupCommand.ConfiguredMode(StateDirectory.Open(dir)));
        Assert.Equal(0, SetupCommand.Run(options, NoClient, "/tmp/atf", homePath: temp.File("home"), interactive: false));
        Assert.Equal("headless", SetupCommand.ConfiguredMode(StateDirectory.Open(dir)));
    }

    [Theory]
    [InlineData("/tmp/atf", "safe/state")]
    [InlineData("safe/atf", "/tmp/atf-state")]
    [InlineData(".worktrees/build/atf", "safe/state")]
    [InlineData("artifacts/build/atf", "safe/state")]
    public void UnsafeRegistrationPathsRequireForce(string binaryPart, string statePart)
    {
        using var temp = new TempStateDir();
        // The temporary home is isolated even when checking a non-temporary path.
        var binary = Path.IsPathRooted(binaryPart) ? binaryPart : Path.Combine("/home", binaryPart);
        var state = Path.IsPathRooted(statePart) ? statePart : Path.Combine("/home", statePart);
        var options = new Dictionary<string, string> { ["mode"] = "headless", ["state-dir"] = state };
        Assert.Equal(64, SetupCommand.Run(options, NoClient, binary, homePath: temp.File("home"), interactive: false));
        Assert.False(File.Exists(Path.Combine(state, "launch-mode.json")));
    }

    [Fact]
    public void IsolatedSetupRefusesInheritedClientHomesEvenWithForce()
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var state = temp.File("state");
        var values = new Dictionary<string, string?>
        {
            ["CODEX_HOME"] = "/home/owner/.codex",
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, ".claude"),
        };
        string? Env(string name) => values.GetValueOrDefault(name);

        var options = new Dictionary<string, string> { ["mode"] = "headless", ["state-dir"] = state };
        static (int, string) NoCommands(string _, IReadOnlyList<string> __) => throw new InvalidOperationException("client invoked");
        Assert.Equal(64, SetupCommand.Run(options, NoCommands, "/tmp/atf", homePath: home, clientEnvironment: Env));
        options["force"] = "true";
        Assert.Equal(64, SetupCommand.Run(options, NoCommands, "/tmp/atf", homePath: home, clientEnvironment: Env));
        Assert.False(Directory.Exists(state));
        Assert.Contains("CODEX_HOME", SetupCommand.ClientConfigProblem(state, home, force: true, Env));
        Assert.Contains("CODEX_HOME", SetupCommand.ClientConfigProblem(Path.Combine(home, ".local", "state", "agentteamforge"), home, force: false, Env));
        values["CODEX_HOME"] = Path.Combine(home, ".codex");
        values["CLAUDE_CONFIG_DIR"] = "/home/owner/.claude";
        Assert.Contains("CLAUDE_CONFIG_DIR", SetupCommand.ClientConfigProblem(state, home, force: true, Env));
        values["CLAUDE_CONFIG_DIR"] = Path.Combine(home, ".claude");
        Assert.Null(SetupCommand.ClientConfigProblem(state, home, force: true, Env));
        Assert.Null(SetupCommand.ClientConfigProblem(Path.Combine(home, ".local", "state", "agentteamforge") + Path.DirectorySeparatorChar, home, force: false, Env));
    }

    [Fact]
    public void TemporaryStateNeverRegistersInRealHomeEvenWithForce()
    {
        using var temp = new TempStateDir();
        var state = temp.File("state");
        var realHome = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "home", "owner");
        var options = new Dictionary<string, string> { ["mode"] = "headless", ["state-dir"] = state, ["force"] = "true" };
        static (int, string) NoCommands(string _, IReadOnlyList<string> __) => throw new InvalidOperationException("client invoked");
        Assert.Equal(64, SetupCommand.Run(options, NoCommands, "/tmp/atf", homePath: realHome, clientEnvironment: _ => null));
        Assert.False(Directory.Exists(state));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("pi")]
    public void SetupRegistersEachClientAlone(string installedClient)
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var state = temp.File("state");
        var extension = temp.File("pi-wake");
        Directory.CreateDirectory(extension);
        File.WriteAllText(Path.Combine(extension, "package.json"), "{}");
        var piSettings = Path.Combine(home, ".pi", "agent", "settings.json");
        var registrations = new List<string>();
        (int, string) Runner(string tool, IReadOnlyList<string> args)
        {
            if (args[0] == "--version")
            {
                return (tool == installedClient ? 0 : 127, "test");
            }
            if (tool == "pi")
            {
                var settings = File.Exists(piSettings)
                    ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(piSettings))!.AsObject()
                    : [];
                if (settings["packages"] is null)
                {
                    settings["packages"] = new System.Text.Json.Nodes.JsonArray(
                        (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(args[1]));
                }
                else
                {
                    settings["packages"]!.AsArray().Add((System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(args[1]));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(piSettings)!);
                File.WriteAllText(piSettings, settings.ToJsonString());
                return (0, "");
            }
            if (args[1] == "get")
            {
                return (1, "missing");
            }
            registrations.Add(tool);
            return (0, "");
        }
        var options = new Dictionary<string, string> { ["mode"] = "headless", ["state-dir"] = state, ["force"] = "true" };
        Assert.Equal(0, SetupCommand.Run(options, Runner, "/tmp/atf", homePath: home, extensionPath: extension));
        if (installedClient == "pi")
        {
            Assert.True(File.Exists(Path.Combine(home, ".pi", "agent", "mcp.json")));
            Assert.Contains("npm:pi-mcp-adapter", File.ReadAllText(piSettings));
        }
        else
        {
            Assert.Equal([installedClient], registrations);
        }
    }

    [Fact]
    public void PartialRegistrationFailureCanBeRerun()
    {
        using var temp = new TempStateDir();
        var state = temp.File("state");
        var registered = new HashSet<string>();
        var failCodex = true;
        (int, string) Runner(string tool, IReadOnlyList<string> args)
        {
            if (args[0] == "--version")
            {
                return (tool == "pi" ? 127 : 0, "test");
            }
            if (args[1] == "get")
            {
                return registered.Contains(tool)
                    ? (0, $"Scope: User config\n enabled: true\n Command: /tmp/atf\n Args: mcp --state-dir {state}\n") : (1, "missing");
            }
            if (tool == "codex" && failCodex)
            {
                return (1, new string('x', 500) + " stderr detail");
            }
            registered.Add(tool);
            return (0, "");
        }
        var options = new Dictionary<string, string> { ["mode"] = "headless", ["state-dir"] = state, ["force"] = "true" };
        var home = temp.File("home");
        var originalError = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        int firstResult;
        try
        {
            firstResult = SetupCommand.Run(options, Runner, "/tmp/atf", homePath: home);
        }
        finally
        {
            Console.SetError(originalError);
        }
        Assert.Equal(0, firstResult);
        Assert.Contains("claude", registered);
        Assert.Contains("codex mcp add agentteamforge -- /tmp/atf mcp --state-dir", error.ToString());
        failCodex = false;
        Assert.Equal(0, SetupCommand.Run(new Dictionary<string, string> { ["state-dir"] = state, ["force"] = "true" },
            Runner, "/tmp/atf", homePath: home, interactive: false));
        Assert.Equal(2, registered.Count);
        Assert.Equal("headless", SetupCommand.ConfiguredMode(StateDirectory.Open(state)));
    }

    [Fact]
    public void ManualRegistrationCommandIsShellQuoted()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var temp = new TempStateDir();
        var state = temp.File("state;id $(x) 'q'");
        static (int, string) Runner(string tool, IReadOnlyList<string> args) =>
            args[0] == "--version" ? (tool == "codex" ? 0 : 127, "test") : args[1] == "get" ? (1, "missing") : (1, "boom");
        var originalError = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try
        {
            Assert.Equal(0, SetupCommand.Run(new Dictionary<string, string> { ["mode"] = "headless", ["state-dir"] = state, ["force"] = "true" },
                Runner, "/tmp/atf", homePath: temp.File("home")));
        }
        finally
        {
            Console.SetError(originalError);
        }
        Assert.Contains($"--state-dir '{state.Replace("'", "'\\''")}'", error.ToString());
    }

    [Fact]
    public void MalformedClaudeSettingsStillFailSetup()
    {
        using var temp = new TempStateDir();
        var state = temp.File("state");
        var home = temp.File("home");
        var settings = Path.Combine(home, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, "{ not json");
        static (int, string) Runner(string tool, IReadOnlyList<string> args) =>
            args[0] == "--version" ? (tool == "claude" ? 0 : 127, "test") : args[1] == "get" ? (1, "missing") : (0, "");
        Assert.Equal(1, SetupCommand.Run(new Dictionary<string, string> { ["mode"] = "headless", ["state-dir"] = state, ["force"] = "true" },
            Runner, "/tmp/atf", homePath: home));
    }

    [Fact]
    public void PiAdapterFailureLeavesNoRegistrationAndRerunSucceeds()
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var state = temp.File("state");
        var extension = temp.File("pi-wake");
        var settingsPath = Path.Combine(home, ".pi", "agent", "settings.json");
        var mcpPath = Path.Combine(home, ".pi", "agent", "mcp.json");
        Directory.CreateDirectory(extension);
        File.WriteAllText(Path.Combine(extension, "package.json"), "{}");
        var fail = true;
        (int, string) Runner(string tool, IReadOnlyList<string> args)
        {
            if (args[0] == "--version")
            {
                return (tool == "pi" ? 0 : 127, "test");
            }
            if (fail)
            {
                return (1, "network unavailable");
            }
            var settings = File.Exists(settingsPath)
                ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject()
                : [];
            if (settings["packages"] is null)
            {
                settings["packages"] = new System.Text.Json.Nodes.JsonArray(
                    (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(args[1]));
            }
            else
            {
                settings["packages"]!.AsArray().Add((System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(args[1]));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, settings.ToJsonString());
            return (0, "");
        }
        var options = new Dictionary<string, string> { ["mode"] = "headless", ["state-dir"] = state, ["force"] = "true" };
        Assert.Equal(1, SetupCommand.Run(options, Runner, "/tmp/atf", homePath: home, extensionPath: extension));
        Assert.False(File.Exists(mcpPath));
        fail = false;
        Assert.Equal(0, SetupCommand.Run(options, Runner, "/tmp/atf", homePath: home, extensionPath: extension));
        Assert.Contains("npm:pi-mcp-adapter", File.ReadAllText(settingsPath));
        Assert.Contains("agentteamforge", File.ReadAllText(mcpPath));
    }

    [Fact]
    public void Setup_configures_web_port_and_preserves_it_on_rerun()
    {
        using var temp = new TempStateDir();
        var dir = temp.File("state");
        var options = new Dictionary<string, string> { ["mode"] = "headless", ["state-dir"] = dir, ["web-port"] = "9123", ["force"] = "true" };
        Assert.Equal(64, SetupCommand.Run(new Dictionary<string, string>(options) { ["web-port"] = "0" }));
        Assert.False(Directory.Exists(dir));

        Assert.Equal(0, SetupCommand.Run(options, NoClient, executablePath: "/tmp/atf", homePath: temp.File("home")));
        var state = StateDirectory.Open(dir);
        Assert.Equal(9123, SetupCommand.ConfiguredWebPort(state));
        options.Remove("web-port");
        Assert.Equal(0, SetupCommand.Run(options, NoClient, executablePath: "/tmp/atf", homePath: temp.File("home")));
        Assert.Equal(9123, SetupCommand.ConfiguredWebPort(state));
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
            ["force"] = "true",
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
    public void CheckRequiresClaudeInboundWithoutChangingSettings()
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var binary = temp.File("atf");
        var state = temp.File("state");
        var settings = Path.Combine(home, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        const string original = """{"theme":"dark","crossSessionInbound":"refuse"}""";
        File.WriteAllText(settings, original);
        (int, string) Runner(string tool, IReadOnlyList<string> args) => tool != "claude"
            ? (127, "") : args[0] == "--version" ? (0, "test")
            : (0, $"Scope: User config\n  enabled: true\n  Command: {binary}\n  Args: mcp --state-dir {state}\n");

        Assert.False(ClientSetup.Reconcile(binary, state, home, settings, null, Runner, apply: false));
        Assert.Equal(original, File.ReadAllText(settings));
        SetupCommand.EnableClaudeInbound(settings);
        Assert.True(ClientSetup.Reconcile(binary, state, home, settings, null, Runner, apply: false));
        using var configured = JsonDocument.Parse(File.ReadAllText(settings));
        Assert.Equal("dark", configured.RootElement.GetProperty("theme").GetString());
    }

    [Fact]
    public void ClaudeSettingsWithUtf8BomAreAppliedAndChecked()
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var binary = temp.File("atf");
        var state = temp.File("state");
        var settings = Path.Combine(home, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(settings, """{"theme":"dark"}""", encoding);
        Assert.True(File.ReadAllBytes(settings).AsSpan().StartsWith(encoding.GetPreamble()));
        (int, string) Runner(string tool, IReadOnlyList<string> args) => tool != "claude"
            ? (127, "") : args[0] == "--version" ? (0, "test")
            : (0, $"Scope: User config\n  enabled: true\n  Command: {binary}\n  Args: mcp --state-dir {state}\n");

        Assert.True(ClientSetup.Reconcile(binary, state, home, settings, null, Runner, apply: true));
        var rewritten = File.ReadAllBytes(settings);
        Assert.False(rewritten.AsSpan().StartsWith(encoding.GetPreamble()));
        using var configured = JsonDocument.Parse(rewritten);
        Assert.Equal("dark", configured.RootElement.GetProperty("theme").GetString());
        Assert.Equal("accept", configured.RootElement.GetProperty("crossSessionInbound").GetString());

        File.WriteAllText(settings, """{"theme":"dark","crossSessionInbound":"accept"}""", encoding);
        var beforeCheck = File.ReadAllBytes(settings);
        Assert.True(beforeCheck.AsSpan().StartsWith(encoding.GetPreamble()));
        Assert.True(ClientSetup.Reconcile(binary, state, home, settings, null, Runner, apply: false));
        Assert.Equal(beforeCheck, File.ReadAllBytes(settings));
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
    public async Task StartedDaemonIsNotADescendantOfItsStarter()
    {
        if (!OperatingSystem.IsLinux()) { return; }

        using var rig = new SpikeRig();
        await rig.InitAsync();
        // The in-process starter is this test process, as an MCP bridge is for its autostarted daemon.
        Assert.Equal(0, await SetupCommand.StartAsync(new Dictionary<string, string> { ["state-dir"] = rig.StateDir }, SpikeRig.Binary, quiet: true));

        var pid = Assert.IsType<int>(DaemonLock.ReadOwnerPid(StateDirectory.Open(rig.StateDir).LockFile));
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        var ppid = int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[1]);
        Assert.NotEqual(Environment.ProcessId, ppid);
    }

    [Fact]
    public async Task StartWithoutSetsidStillStartsAndWarnsInDaemonLog()
    {
        if (!OperatingSystem.IsLinux()) { return; }

        using var rig = new SpikeRig();
        await rig.InitAsync();
        Assert.Equal(0, await SetupCommand.StartAsync(new Dictionary<string, string> { ["state-dir"] = rig.StateDir },
            SpikeRig.Binary, quiet: true, setsidSearch: []));
        Assert.Contains("util-linux", File.ReadAllText(Path.Combine(rig.StateDir, "daemon.log")));
        Assert.NotNull(DaemonLock.ReadOwnerPid(StateDirectory.Open(rig.StateDir).LockFile));
    }

    [Theory]
    [InlineData("profile.json")]
    [InlineData("launch-mode.json")]
    public async Task CorruptStartupJsonFailsCleanlyForClientAndDaemon(string name)
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var path = Path.Combine(rig.StateDir, name);
        File.WriteAllText(path, "{invalid json");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, StateDirectory.PrivateFile);
        }

        foreach (var command in new[] { "start", "daemon" })
        {
            var (exit, _, error) = await rig.RunToExitAsync([command, "--state-dir", rig.StateDir]);
            Assert.Equal(78, exit);
            Assert.Contains(path, error);
            Assert.Contains("atf setup or atf doctor", error);
            Assert.DoesNotContain("Unhandled exception", error);
        }
    }

    [Fact]
    public void RequestedUnavailableHerdrDoesNotWriteMode()
    {
        using var temp = new TempStateDir();
        var dir = temp.File("state");
        Assert.Equal(64, SetupCommand.Run(new Dictionary<string, string> { ["mode"] = "herdr", ["state-dir"] = dir },
            NoClient, "/tmp/atf"));
        Assert.False(File.Exists(Path.Combine(dir, "launch-mode.json")));
    }

    [Fact]
    public void SetupRejectsFakeOnlyProfile()
    {
        using var temp = new TempStateDir();
        var dir = temp.File("state");
        Assert.Equal(0, InitCommand.Run(dir, testProfile: true, queueLimit: null, maxRuntimeSeconds: null));

        Assert.Equal(78, SetupCommand.Run(new Dictionary<string, string> { ["mode"] = "headless", ["state-dir"] = dir, ["force"] = "true" },
            (_, _) => throw new InvalidOperationException(), "/tmp/atf", homePath: temp.File("home")));
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
                line => line.StartsWith("[atf-daemon] ", StringComparison.Ordinal) && line.Contains(" ready pid=", StringComparison.Ordinal));
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
    public async Task StartedDaemonHasItsOwnSessionAndOnlyExplicitStandardHandles()
    {
        if (!OperatingSystem.IsLinux()) { return; }

        using var rig = new SpikeRig();
        await rig.InitAsync();
        var sentinelPath = Path.Combine(Path.GetDirectoryName(rig.StateDir)!, "caller-fd");
        using var sentinel = File.OpenHandle(sentinelPath, FileMode.CreateNew, FileAccess.ReadWrite);
        var start = new ProcessStartInfo(SpikeRig.Binary)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            InheritedHandles = [sentinel],
        };
        foreach (var arg in new[] { "start", "--state-dir", rig.StateDir }) { start.ArgumentList.Add(arg); }
        using var caller = Process.Start(start)!;
        var output = caller.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = caller.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var deadline = new CancellationTokenSource(Bounded.ScenarioDeadline);
        await caller.WaitForExitAsync(deadline.Token);
        Assert.True(caller.ExitCode == 0, $"atf start exited {caller.ExitCode}: {await output} {await error}");

        var pid = Assert.IsType<int>(DaemonLock.ReadOwnerPid(StateDirectory.Open(rig.StateDir).LockFile));
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        Assert.Equal(pid, int.Parse(fields[2])); // process group
        Assert.Equal(pid, int.Parse(fields[3])); // session
        Assert.Contains("Umask:\t0077", File.ReadAllLines($"/proc/{pid}/status"));

        var fdDir = $"/proc/{pid}/fd";
        Assert.Equal("/dev/null", new FileInfo(Path.Combine(fdDir, "0")).LinkTarget);
        var log = Path.Combine(rig.StateDir, "daemon.log");
        Assert.Equal(log, new FileInfo(Path.Combine(fdDir, "1")).LinkTarget);
        Assert.Equal(log, new FileInfo(Path.Combine(fdDir, "2")).LinkTarget);
        Assert.Equal(StateDirectory.PrivateFile, File.GetUnixFileMode(log));
        Assert.DoesNotContain("agentteamforge-daemon-", File.ReadAllText($"/proc/{pid}/cgroup"));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(fdDir),
            fd => new FileInfo(fd).LinkTarget == sentinelPath);
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
        Assert.Contains("KillMode=process", unit);
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

    [Theory]
    [InlineData("/tmp/atf")]
    [InlineData("/home/safe/.worktrees/build/atf")]
    [InlineData("/home/safe/atf")]
    public void AutostartRequiresForceForUnsafeBinaryOrState(string binary)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temp = new TempStateDir();
        var dir = temp.File("state");
        var home = temp.File("home");
        Assert.Equal(0, SetupCommand.Run(new Dictionary<string, string> { ["mode"] = "headless", ["state-dir"] = dir, ["force"] = "true" },
            NoClient, "/tmp/atf", homePath: home, interactive: false));

        var calls = 0;
        (int, string) Runner(string _, IReadOnlyList<string> __)
        {
            calls++;
            return (0, "");
        }
        Assert.Equal(64, SetupCommand.Run(new Dictionary<string, string> { ["autostart"] = "on", ["apply"] = "true", ["state-dir"] = dir },
            Runner, binary, homePath: home));
        Assert.False(LoginAutostart.IsInstalled(home, "linux"));
        Assert.Equal(0, calls);

        Assert.Equal(0, SetupCommand.Run(new Dictionary<string, string> { ["autostart"] = "on", ["apply"] = "true", ["state-dir"] = dir, ["force"] = "true" },
            Runner, binary, homePath: home));
        Assert.True(LoginAutostart.IsInstalled(home, "linux"));

        Assert.Equal(0, SetupCommand.Run(new Dictionary<string, string> { ["autostart"] = "off", ["apply"] = "true", ["state-dir"] = dir },
            Runner, binary, homePath: home));
        Assert.False(LoginAutostart.IsInstalled(home, "linux"));
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
