using System.Diagnostics;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Tests.Features.Agents.Backends;

public sealed class OrphanedBackendProcessTests
{
    [Fact]
    public void Only_processes_carrying_an_exact_run_marker_are_killed_including_descendants()
    {
        var run = Guid.NewGuid().ToString("N");
        var marked = Start(info => OrphanedBackendProcess.Mark(info, run), "sleep 300 & echo $!; wait");
        var otherRun = Start(info => OrphanedBackendProcess.Mark(info, run + "0"), "exec sleep 300");
        var lookalike = Start(info => info.Environment["X_ATF_RUN_CORRELATION"] = run, "exec sleep 300");
        try
        {
            var grandchild = int.Parse(marked.StandardOutput.ReadLine()!, System.Globalization.CultureInfo.InvariantCulture);

            Assert.Equal(2, OrphanedBackendProcess.TerminateMarked([run]));

            Assert.True(marked.WaitForExit(TimeSpan.FromSeconds(10)));
            Assert.True(SpinWait.SpinUntil(() => !IsAlive(grandchild), TimeSpan.FromSeconds(10)));
            Assert.False(otherRun.HasExited);
            Assert.False(lookalike.HasExited);
        }
        finally
        {
            foreach (var process in new[] { marked, otherRun, lookalike })
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.Dispose();
            }
        }
    }

    static Process Start(Action<ProcessStartInfo> tag, string script)
    {
        var info = new ProcessStartInfo("/bin/sh", ["-c", script]) { RedirectStandardOutput = true };
        tag(info);
        return Process.Start(info)!;
    }

    static bool IsAlive(int pid)
    {
        try
        {
            // A killed grandchild may linger as a zombie until init reaps it.
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)..][0] != 'Z';
        }
        catch (IOException)
        {
            return false;
        }
    }
}
