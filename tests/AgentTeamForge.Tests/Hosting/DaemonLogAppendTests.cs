using System.Text.RegularExpressions;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Hosting;

/// <summary>
/// daemon.log has several writers in different processes (the daemon, its starter, a daemon that lost the
/// lock). Each must append whole lines at the real end of file, never at a stale private offset.
/// </summary>
public sealed partial class DaemonLogAppendTests
{
    [Fact]
    public async Task Daemon_and_another_process_append_whole_lines_without_overwriting_each_other()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var path = Path.Combine(rig.StateDir, "daemon.log");
        using var writer = AppendOnlyFile.Open(path);
        using var stop = new CancellationTokenSource();
        var written = 0;
        var other = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                writer.WriteLine($"other {written} {new string('x', 200)}");
                written++;
            }
        }, TestContext.Current.CancellationToken);

        await rig.StartDaemonAsync();
        Assert.Equal(0, (await rig.RunToExitAsync(["stop", "--state-dir", rig.StateDir])).Exit);
        await stop.CancelAsync();
        await other;

        var lines = File.ReadAllLines(path);
        Assert.All(lines, line => Assert.True(OtherLine().IsMatch(line) || DaemonLine().IsMatch(line), $"damaged line: {line}"));
        Assert.Equal(Enumerable.Range(0, written).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            lines.Where(line => line.StartsWith("other ", StringComparison.Ordinal)).Select(line => line.Split(' ')[1]));
        Assert.Contains(lines, line => line.Contains(" ready pid=", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.EndsWith(" stopped reason=requested_shutdown", StringComparison.Ordinal));
    }

    [Fact]
    public void Separately_opened_handles_append_instead_of_overwriting()
    {
        using var temp = new TempStateDir();
        var path = temp.File("shared.log");
        using var first = AppendOnlyFile.Open(path);
        using var second = AppendOnlyFile.Open(path);

        first.WriteLine("one");
        second.WriteLine("two");
        first.WriteLine("three");

        Assert.Equal(["one", "two", "three"], File.ReadAllLines(path));
    }

    [GeneratedRegex(@"^other \d+ x{200}$")]
    private static partial Regex OtherLine();

    [GeneratedRegex(@"^\[atf-daemon\] \d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z \S")]
    private static partial Regex DaemonLine();
}
