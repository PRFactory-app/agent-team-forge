using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Scenarios;

/// <summary>
/// Review B1/B2 against the real daemon: no ready-looking daemon without a
/// dispatcher, invalid limits refused before readiness, and a child that
/// stalls before reading its instruction is bounded by the runtime deadline.
/// </summary>
[Trait("Category", "Scenario")]
public sealed class CoreFaultScenarios
{
    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("86401")]
    public async Task Init_refuses_an_unbounded_runtime_and_creates_nothing(string seconds)
    {
        using var rig = new SpikeRig();

        var (exit, _, _) = await rig.RunToExitAsync(["init", "--state-dir", rig.StateDir, "--test-profile", "--max-runtime-seconds", seconds]);

        Assert.NotEqual(0, exit);
        Assert.False(Directory.Exists(rig.StateDir) && Directory.EnumerateFileSystemEntries(rig.StateDir).Any());
    }

    [Fact]
    public async Task Daemon_refuses_readiness_for_an_edited_invalid_profile()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync(maxRuntimeSeconds: 5);
        var profile = Path.Combine(rig.StateDir, "profile.json");
        File.WriteAllText(profile, File.ReadAllText(profile).Replace("\"max_fake_runtime_seconds\": 5", "\"max_fake_runtime_seconds\": -2", StringComparison.Ordinal));

        var (exit, _, stderr) = await rig.RunToExitAsync(["daemon", "--state-dir", rig.StateDir]);

        Assert.NotEqual(0, exit);
        Assert.DoesNotContain("ready", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Daemon_survives_stray_signals_and_logs_them()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var daemon = await rig.StartDaemonAsync();

        foreach (var name in new[] { "HUP", "USR1", "USR2", "ALRM" })
        {
            using var kill = System.Diagnostics.Process.Start("kill", ["-" + name, daemon.Id.ToString()])!;
            await kill.WaitForExitAsync(TestContext.Current.CancellationToken);
            await Bounded.Until(() => rig.DaemonLog.Any(l => l.Contains($"received SIG{name}; ignored", StringComparison.Ordinal)),
                $"SIG{name} to be logged");
        }

        Assert.False(daemon.HasExited);
        Assert.True((await rig.SubmitAsync("k-signals", "x")).Ok);
    }

    [Fact]
    public async Task Dispatcher_fault_stops_admission_instead_of_leaving_a_ready_daemon()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var daemon = await rig.StartDaemonAsync("--test-fail-at", DurabilityCheckpoints.AttemptAfterCommit);

        var accepted = await rig.SubmitAsync("k-fault", "x");
        Assert.Equal("accepted", accepted.Outcome);

        await Bounded.Until(() => daemon.HasExited, "daemon to stop after a dispatcher fault");
        Assert.NotEqual(0, daemon.ExitCode);
        await Bounded.Until(() => rig.DaemonLog.Any(l => l.Contains("dispatcher_halted", StringComparison.Ordinal)),
            "dispatcher halt log to drain");
        // The next client call starts a fresh daemon; the failed dispatcher
        // never remains available to accept work.
        try
        {
            Assert.True((await rig.SubmitAsync("k-after-fault", "x")).Ok);
            var job = (await rig.GetAsync(accepted.Job!.JobId)).Job!;
            Assert.Equal(JobStatus.NeedsReconciliation, job.Status);
            Assert.Equal(1, job.Attempts);
            Assert.Equal(0, rig.Invocations(job.JobId));
        }
        finally
        {
            await rig.RunToExitAsync(["stop", "--state-dir", rig.StateDir]);
        }
    }

    [Fact]
    public async Task Child_stalled_before_reading_its_instruction_is_bounded_and_killed_via_held_handle()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync(maxRuntimeSeconds: 2);
        await rig.StartDaemonAsync();

        var accepted = await rig.SubmitAsync("k-stall", "x", behavior: FakeBehavior.StallBeforeRead);
        var jobId = accepted.Job!.JobId;
        var marker = Path.Combine(rig.BarrierDir, $"stalled-{accepted.Job.JobId}");
        await Bounded.Until(() => File.Exists(marker), "fake child to stall before reading");
        var pid = int.Parse(File.ReadAllText(marker), System.Globalization.CultureInfo.InvariantCulture);

        var done = await rig.WaitForStatusAsync(jobId, JobStatus.NeedsReconciliation, JobStatus.Completed, JobStatus.Failed);
        Assert.Equal(JobStatus.NeedsReconciliation, done.Job!.Status);
        Assert.Equal("backend_timeout", done.Job.ReasonCode);
        Assert.Equal(1, done.Job.Attempts);
        Assert.Equal(0, rig.Invocations(jobId));
        await Bounded.Until(() => !Directory.Exists($"/proc/{pid}"), "stalled child to be terminated");

        // The dispatcher is still live: later work progresses.
        var next = await rig.SubmitAsync("k-next", "y");
        await rig.WaitForStatusAsync(next.Job!.JobId, JobStatus.Completed);
    }
}
