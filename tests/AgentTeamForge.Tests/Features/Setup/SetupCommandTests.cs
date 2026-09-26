using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Setup;

public sealed class SetupCommandTests
{
    [Fact]
    public void SetupRequiresExplicitMode()
    {
        using var temp = new TempStateDir();
        var options = new Dictionary<string, string> { ["state-dir"] = temp.File("state") };

        Assert.Equal(64, SetupCommand.Run(options, (_, _) => throw new InvalidOperationException()));
        Assert.False(Directory.Exists(options["state-dir"]));
    }

    [Fact]
    public void ApplyRegistersEachHostOnlyOnce()
    {
        using var temp = new TempStateDir();
        var options = new Dictionary<string, string>
        {
            ["mode"] = "headless",
            ["state-dir"] = temp.File("state"),
            ["apply"] = "true",
        };
        var registered = new HashSet<string>();
        var adds = 0;
        int Runner(string tool, IReadOnlyList<string> args)
        {
            Assert.Equal("mcp", args[0]);
            if (args[1] == "get")
            {
                return registered.Contains(tool) ? 0 : 1;
            }

            Assert.Equal("add", args[1]);
            Assert.Equal("agentteamforge", args.Contains("--scope") ? args[4] : args[2]);
            Assert.Contains("--state-dir", args);
            registered.Add(tool);
            adds++;
            return 0;
        }

        Assert.Equal(0, SetupCommand.Run(options, Runner, "/tmp/atf"));
        Assert.Equal(0, SetupCommand.Run(options, Runner, "/tmp/atf"));
        Assert.Equal(2, adds);
        Assert.Contains("headless", File.ReadAllText(Path.Combine(options["state-dir"], "launch-mode.json")));
    }

    [Fact]
    public async Task StartDetectsOwnedRunningDaemon()
    {
        using var temp = new TempStateDir();
        var dir = temp.File("state");
        Assert.Equal(0, SetupCommand.Run(new Dictionary<string, string> { ["mode"] = "headless", ["state-dir"] = dir },
            (_, _) => throw new InvalidOperationException(), "/tmp/atf"));
        var state = StateDirectory.Open(dir);
        using var daemonLock = DaemonLock.TryAcquire(state.LockFile);
        Assert.NotNull(daemonLock);
        daemonLock.WriteOwnerPid();

        Assert.Equal(0, await SetupCommand.StartAsync(new Dictionary<string, string> { ["state-dir"] = dir }, "/missing/atf"));
    }

    [Fact]
    public async Task HerdrModeDoesNotStartHeadlessBackends()
    {
        using var temp = new TempStateDir();
        var dir = temp.File("state");
        Assert.Equal(0, SetupCommand.Run(new Dictionary<string, string> { ["mode"] = "herdr", ["state-dir"] = dir },
            (_, _) => throw new InvalidOperationException(), "/tmp/atf"));

        var state = StateDirectory.Open(dir);
        Assert.Equal(78, await SetupCommand.StartAsync(new Dictionary<string, string> { ["state-dir"] = dir }, "/missing/atf"));
        Assert.Equal(78, await DaemonCommand.RunAsync(state, null, null));
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

        var (firstExit, firstOutput, firstError) = await rig.RunToExitAsync(["stop", "--state-dir", rig.StateDir]);
        Assert.Equal(0, firstExit);
        Assert.Empty(firstError);
        Assert.Contains($"Stopped daemon {daemon.Id}.", firstOutput);
        await Bounded.Until(() => daemon.HasExited, "stopped daemon exit");

        var (secondExit, secondOutput, secondError) = await rig.RunToExitAsync(["stop", "--state-dir", rig.StateDir]);
        Assert.Equal(0, secondExit);
        Assert.Empty(secondError);
        Assert.Contains("Daemon is not running.", secondOutput);
    }
}
