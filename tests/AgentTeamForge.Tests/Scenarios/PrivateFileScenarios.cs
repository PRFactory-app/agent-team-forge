using System.Diagnostics;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Scenarios;

/// <summary>
/// Real-process check (also run against the published binary) that a special
/// file in place of a private state file fails closed instead of hanging.
/// </summary>
[Trait("Category", "Scenario")]
public sealed class PrivateFileScenarios
{
    [Theory]
    [InlineData("operator.key", "daemon")]
    [InlineData("operator.key", "client")]
    [InlineData("profile.json", "daemon")]
    public async Task Fifo_state_file_fails_closed_without_hanging(string file, string role)
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var path = Path.Combine(rig.StateDir, file);
        File.Delete(path);
        using (var mkfifo = Process.Start(new ProcessStartInfo("mkfifo", ["-m", "600", path]) { UseShellExecute = false })!)
        {
            Assert.True(mkfifo.WaitForExit(TimeSpan.FromSeconds(5)), "mkfifo timed out");
            Assert.Equal(0, mkfifo.ExitCode);
        }

        string[] args = role == "daemon"
            ? ["daemon", "--state-dir", rig.StateDir]
            : ["client", "get", "--state-dir", rig.StateDir, "--job", "missing"];
        var (exit, _, stderr) = await rig.RunToExitAsync(args);

        Assert.Equal(78, exit);
        Assert.Contains("private_file_unsafe", stderr, StringComparison.Ordinal);
    }
}
