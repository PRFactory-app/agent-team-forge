using System.Diagnostics;
using System.Globalization;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Backends;

public sealed class RunProcessSnapshotsTests
{
    [Fact]
    public void Recorded_platform_binary_is_killed_after_every_marked_ancestor_died()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        using var state = new TempStateDir();
        var run = Guid.NewGuid().ToString("N");
        // A marked agent (visible) whose tool chain is platform binaries (hidden): sh -> sleep.
        using var agent = Start(run, $"/bin/sh -c 'sleep 300 & echo $!; wait' & wait; exec '{MarkerVisibleExecutable.Sleep}' 300");
        var tool = int.Parse(agent.StandardOutput.ReadLine()!, CultureInfo.InvariantCulture);
        var shell = DarwinProcess.ParentPid(tool)!.Value;
        using var bystander = Process.Start(new ProcessStartInfo("/bin/sleep", "300") { UseShellExecute = false })!;
        try
        {
            RunProcessSnapshots.Capture(state.Path, [new(agent.Id, run)], DarwinProcess.Table());
            // The intermediate shell dies first: its child is reparented away from the agent's tree.
            Assert.True(DarwinProcess.SignalIfSame(shell, DarwinProcess.CreationToken(shell)!.Value, 9));
            Assert.True(SpinWait.SpinUntil(() => DarwinProcess.ParentPid(tool) == 1, TimeSpan.FromSeconds(10)));
            RunProcessSnapshots.Capture(state.Path, [new(agent.Id, run)], DarwinProcess.Table());
            // Still recorded although no longer in the agent's tree.
            Assert.Contains(File.ReadAllLines(Path.Combine(state.Path, run)), line => line.StartsWith($"{tool} ", StringComparison.Ordinal));
            OwnedProcessTermination.Kill(agent);
            Assert.True(agent.WaitForExit(10_000));

            Assert.Equal(1, DarwinProcess.ParentPid(tool));
            Assert.Equal(1, RunProcessSnapshots.Kill(state.Path, [run]));

            Assert.True(SpinWait.SpinUntil(() => DarwinProcess.Info(tool) is not { IsZombie: false }, TimeSpan.FromSeconds(10)));
            Assert.False(bystander.HasExited);
            Assert.Empty(Directory.EnumerateFiles(state.Path));
        }
        finally
        {
            Cleanup(tool);
            foreach (var process in new[] { agent, bystander })
            {
                try { OwnedProcessTermination.Kill(process); } catch (InvalidOperationException) { }
            }
        }
    }

    [Fact]
    public void A_recorded_pid_with_another_creation_token_is_never_signalled()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        using var state = new TempStateDir();
        using var other = Process.Start(new ProcessStartInfo("/bin/sleep", "300") { UseShellExecute = false })!;
        try
        {
            var token = DarwinProcess.CreationToken(other.Id)!.Value;
            // A reused PID: the record names the same PID with the token of the process that used to have it.
            File.WriteAllText(Path.Combine(state.Path, "reused"), $"{other.Id} {token + 1}\n{other.Id} {token + 1}\n");

            Assert.Equal(0, RunProcessSnapshots.Kill(state.Path, ["reused"]));

            Assert.False(other.WaitForExit(200));
        }
        finally { OwnedProcessTermination.Kill(other); }
    }

    [Fact]
    public void A_root_is_recorded_only_when_it_visibly_carries_the_marker_or_has_the_known_token()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        using var state = new TempStateDir();
        var run = Guid.NewGuid().ToString("N");
        using var unmarked = Process.Start(new ProcessStartInfo(MarkerVisibleExecutable.Sleep, "300") { UseShellExecute = false })!;
        try
        {
            var table = DarwinProcess.Table();
            RunProcessSnapshots.Capture(state.Path, [new(unmarked.Id, run)], table);
            RunProcessSnapshots.Capture(state.Path, [new(unmarked.Id, "wrongtoken", DarwinProcess.CreationToken(unmarked.Id) + 1)], table);
            Assert.Empty(Directory.EnumerateFiles(state.Path));

            RunProcessSnapshots.Capture(state.Path, [new(unmarked.Id, run, DarwinProcess.CreationToken(unmarked.Id))], table);
            Assert.Single(Directory.EnumerateFiles(state.Path));
            Assert.Equal(1, RunProcessSnapshots.Kill(state.Path, [run]));
            Assert.True(unmarked.WaitForExit(10_000));
        }
        finally
        {
            try { OwnedProcessTermination.Kill(unmarked); } catch (InvalidOperationException) { }
        }
    }

    static Process Start(string run, string script)
    {
        var info = new ProcessStartInfo(MarkerVisibleExecutable.Shell, ["-c", script]) { RedirectStandardOutput = true, UseShellExecute = false };
        OrphanedBackendProcess.Mark(info, run);
        return Process.Start(info)!;
    }

    static void Cleanup(int pid)
    {
        if (DarwinProcess.CreationToken(pid) is { } token) { DarwinProcess.SignalIfSame(pid, token, 9); }
    }
}
