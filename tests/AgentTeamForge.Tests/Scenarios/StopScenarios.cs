using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Scenarios;

[Trait("Category", "Scenario")]
public sealed class StopScenarios
{
    [Fact]
    public async Task Stop_succeeds_when_a_waiting_starter_takes_the_lock_the_moment_it_is_released()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Windows stop waits on the process handle; the lock race is a Unix flock one.
        }
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var daemon = await rig.StartDaemonAsync();
        var lockFile = StateDirectory.Open(rig.StateDir).LockFile;
        using var stopWaiting = new CancellationTokenSource();
        // Stands in for a bridge or launchd starting the next daemon while stop is still waiting.
        var successor = Task.Run(async () =>
        {
            while (!stopWaiting.IsCancellationRequested)
            {
                if (DaemonLock.TryAcquire(lockFile) is { } taken)
                {
                    return taken;
                }
                await Task.Delay(1);
            }
            return null;
        }, TestContext.Current.CancellationToken);

        var (exit, stdout, stderr) = await rig.RunToExitAsync(["stop", "--state-dir", rig.StateDir]);
        await stopWaiting.CancelAsync();
        using var held = await successor;

        Assert.True(exit == 0, stderr);
        Assert.Contains($"Stopped daemon {daemon.Id}.", stdout);
        Assert.True(daemon.HasExited);
        Assert.NotNull(held);
    }
}
