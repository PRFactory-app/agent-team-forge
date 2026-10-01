using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;
using ModelContextProtocol.Protocol;

namespace AgentTeamForge.Tests.Scenarios;

/// <summary>
/// Setup, setup --check and uninstall run `claude mcp get`, which spawns `atf mcp` to health-check the
/// registration. That bridge answers initialize and tools/list but never starts a daemon.
/// </summary>
[Trait("Category", "Scenario")]
public sealed class BridgeHealthCheckScenarios
{
    [Fact]
    public async Task Health_check_bridge_reports_the_release_version_and_tools_without_starting_a_daemon()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();

        var (_, client) = await rig.StartBridgeAsync(environment: new Dictionary<string, string> { [JobsMcpBridge.HealthCheckVariable] = "1" });

        // The binary under test may be a stamped release (ATF_HOST_BINARY), so compare with its own --version.
        var (exit, version, _) = await rig.RunToExitAsync(["--version"]);
        Assert.Equal(0, exit);
        Assert.Equal(version.Trim(), "atf " + client.ServerInfo.Version);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, tool => tool.Name == "submit_job");
        var result = await client.CallToolAsync("list_jobs", new Dictionary<string, object?>(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains(IpcProtocol.DaemonUnavailable, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.False(File.Exists(rig.SocketPath));
        Assert.False(File.Exists(Path.Combine(rig.StateDir, "daemon.log")));
        using var free = DaemonLock.TryAcquire(StateDirectory.Open(rig.StateDir).LockFile);
        Assert.NotNull(free);
    }
}
