using System.Diagnostics;
using System.Globalization;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Backends;

public sealed class OwnedProcessTerminationTests : IDisposable
{
    readonly TempStateDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // On macOS the runtime's own tree kill stops the direct child first and its SIGCHLD handler then spins on the
    // stopped child, holding the lock every Process call takes. With other processes starting meanwhile (the daemon,
    // or a parallel test run) the kill never finishes and the child stays stopped, hanging the whole process.
    [Fact]
    public async Task Tree_kill_finishes_and_kills_descendants_while_other_processes_start()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX shell process tree");
        var pidFile = _dir.File("grandchild");
        var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"sleep 300 & echo $! > '{pidFile}'; wait");
        // Disposed only once the kill finished: on the hang every Process call, Dispose included, blocks too.
        var root = Process.Start(start)!;
        await Bounded.Until(() => File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0, "grandchild pid");
        var grandchild = int.Parse(File.ReadAllText(pidFile).Trim(), CultureInfo.InvariantCulture);

        using var stop = new CancellationTokenSource();
        var churn = Enumerable.Range(0, 2).Select(_ => Task.Run(() => Churn(stop.Token), TestContext.Current.CancellationToken)).ToArray();
        var finished = true;
        try
        {
            await Task.Run(() => OwnedProcessTermination.Kill(root), TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            finished = false;
        }
        stop.Cancel();

        Assert.True(finished, "tree kill did not finish");
        await Task.WhenAll(churn);
        Assert.True(root.WaitForExit(10_000), "root still running");
        root.Dispose();
        await Bounded.Until(() => !IsAlive(grandchild), "grandchild exit");
    }

    // A listed descendant can exit and its PID be reused before the traversal signals it; the reused PID's process
    // has another creation token and must be neither stopped nor killed.
    [Fact]
    public async Task Macos_descendant_whose_creation_token_changed_is_never_signalled()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Darwin descendant traversal");
        var pidFile = _dir.File("grandchild");
        var start = new ProcessStartInfo("/bin/sh");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"sleep 300 & echo $! > '{pidFile}'; wait");
        using var root = Process.Start(start)!;
        try
        {
            await Bounded.Until(() => File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0, "grandchild pid");
            var grandchild = int.Parse(File.ReadAllText(pidFile).Trim(), CultureInfo.InvariantCulture);
            var token = DarwinProcess.CreationToken(grandchild)!.Value;

            OwnedProcessTermination.KillDarwinDescendant(grandchild, token + 1);
            Assert.True(IsAlive(grandchild), "a process with another creation token was killed");
            Assert.DoesNotContain("T", State(grandchild), StringComparison.Ordinal);

            OwnedProcessTermination.KillDarwinDescendant(grandchild, token);
            await Bounded.Until(() => !IsAlive(grandchild), "grandchild exit");
        }
        finally
        {
            OwnedProcessTermination.Kill(root);
            Assert.True(root.WaitForExit(10_000), "root still running");
        }
    }

    static string State(int pid)
    {
        var start = new ProcessStartInfo("/bin/ps") { RedirectStandardOutput = true };
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("stat=");
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
        using var ps = Process.Start(start)!;
        var state = ps.StandardOutput.ReadToEnd().Trim();
        ps.WaitForExit();
        return state;
    }

    static void Churn(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var start = new ProcessStartInfo("/bin/sh");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("for i in 1 2 3 4; do true & done; wait");
            using var process = Process.Start(start)!;
            process.WaitForExit();
        }
    }

    static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
