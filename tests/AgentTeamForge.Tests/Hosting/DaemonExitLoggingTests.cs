using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Hosting;

public sealed class DaemonExitLoggingTests
{
    [Fact]
    public async Task Serving_fault_logs_the_stop_with_code_70_and_the_process_exit()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var daemon = await rig.StartDaemonAsync("--test-fail-at", DurabilityCheckpoints.AttemptAfterCommit);

        await rig.SubmitAsync("k-exit-fault", "x");
        await Bounded.Until(() => daemon.HasExited, "daemon to stop after a dispatcher fault");

        Assert.Equal(70, daemon.ExitCode);
        var log = File.ReadAllLines(Path.Combine(rig.StateDir, "daemon.log"));
        Assert.Contains(log, line => line.Contains(" stopped reason=", StringComparison.Ordinal) && line.EndsWith(" code=70", StringComparison.Ordinal));
        Assert.Contains(log, line => line.EndsWith(" process exit code=70", StringComparison.Ordinal));
    }

    [Fact]
    public void Starter_appends_the_recorded_exit_result_to_the_gone_without_stop_line()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var temp = new TempStateDir();
        var state = StateDirectory.Open(temp.Path);
        var log = Path.Combine(temp.Path, "daemon.log");
        File.WriteAllLines(log,
        [
            "[atf-daemon] 2026-01-01T00:00:00.000Z starting pid=4242",
            "[atf-daemon] 2026-01-01T00:00:01.000Z ready pid=4242",
        ]);
        File.WriteAllText(Path.Combine(temp.Path, SystemdUser.ExitFile), "result=signal code=killed status=PWR\n");

        SetupCommand.NoteVanishedDaemon(state);

        Assert.EndsWith(" previous daemon 4242 gone without stop result=signal code=killed status=PWR", File.ReadAllLines(log)[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Realtime_limit_warns_only_for_a_finite_limit()
    {
        Assert.Null(RealtimeLimit.Warning(ulong.MaxValue, ulong.MaxValue));
        Assert.Equal("RLIMIT_RTTIME is 0 us (inherited); the kernel may SIGKILL this daemon — start it via systemd service", RealtimeLimit.Warning(0, 0));
        Assert.Contains("is 500 us", RealtimeLimit.Warning(500, ulong.MaxValue), StringComparison.Ordinal);
    }
}
