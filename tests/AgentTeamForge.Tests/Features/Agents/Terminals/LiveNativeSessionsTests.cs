using System.Diagnostics;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Terminals;

/// <summary>The /proc (Linux) and kern.proc/libproc (macOS) probe of which native session runs under a pane shell, against real processes.</summary>
public sealed class LiveNativeSessionsTests
{
    static Process Start(string script, params string[] args)
    {
        var start = new ProcessStartInfo("sh") { ArgumentList = { "-c", script, "sh" } };
        foreach (var arg in args) { start.ArgumentList.Add(arg); }
        return Process.Start(start)!;
    }

    // What Claude records as procStart: /proc stat ticks on Linux, `ps -o lstart` in UTC on macOS.
    static string RecordedStart(int pid)
    {
        if (OperatingSystem.IsMacOS())
        {
            var ps = new ProcessStartInfo("/bin/ps") { RedirectStandardOutput = true, Environment = { ["TZ"] = "UTC" }, ArgumentList = { "-o", "lstart=", "-p", pid.ToString() } };
            using var process = Process.Start(ps)!;
            var lstart = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return lstart;
        }
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        return stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[22 - 3];
    }

    static bool Probed => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    [Fact]
    public async Task Claude_session_comes_from_the_registry_of_this_exact_process_under_the_shell()
    {
        Assert.SkipUnless(Probed, "reads /proc or Darwin process info");
        using var state = new TempStateDir();
        var sessions = Directory.CreateDirectory(Path.Combine(state.Path, "sessions")).FullName;
        using var shell = Start("sleep 30 & wait");
        try
        {
            await Bounded.Until(() => LiveChild(shell.Id) is not null, "agent child");
            var agent = LiveChild(shell.Id)!.Value;
            File.WriteAllText(Path.Combine(sessions, agent + ".json"), $$"""{"pid":{{agent}},"sessionId":"sess-1","procStart":"{{RecordedStart(agent)}}"}""");
            Assert.Equal(["sess-1"], LiveNativeSessions.Read(InteractiveAgentKind.Claude, shell.Id, state.Path)!);
            // A stale registry entry of a reused PID does not count.
            var stale = OperatingSystem.IsMacOS() ? "Thu Jan  1 00:00:01 1970" : "1";
            File.WriteAllText(Path.Combine(sessions, agent + ".json"), $$"""{"pid":{{agent}},"sessionId":"sess-1","procStart":"{{stale}}"}""");
            Assert.Empty(LiveNativeSessions.Read(InteractiveAgentKind.Claude, shell.Id, state.Path)!);
            // An unreadable registry of a process under the shell makes the whole set unknown; only a
            // well-formed, different start time is stale.
            foreach (var registry in new[] { """{"pid":""", """{"sessionId":"other"}""", """{"sessionId":"other","procStart":"x"}""", """["other"]""" })
            {
                File.WriteAllText(Path.Combine(sessions, agent + ".json"), registry);
                Assert.Null(LiveNativeSessions.Read(InteractiveAgentKind.Claude, shell.Id, state.Path));
            }
            Assert.Null(LiveNativeSessions.Read(InteractiveAgentKind.Pi, shell.Id, state.Path));
        }
        finally { OwnedProcessTermination.Kill(shell); }
    }

    [Fact]
    public void Codex_session_is_the_rollout_its_process_keeps_open()
    {
        Assert.SkipUnless(Probed, "reads /proc or Darwin process info");
        using var state = new TempStateDir();
        var rollout = Path.Combine(state.Path, "rollout-2026-10-01T00-00-00-thread-live.jsonl");
        using var shell = Start("exec 3>>\"$1\"; exec sleep 30", rollout);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            IReadOnlySet<string>? live = null;
            while (DateTime.UtcNow < deadline && live is not { Count: > 0 })
            {
                live = LiveNativeSessions.Read(InteractiveAgentKind.Codex, shell.Id, null);
                Thread.Sleep(50);
            }
            Assert.Equal(["thread-live"], live!);
        }
        finally { OwnedProcessTermination.Kill(shell); }
    }

    [Fact]
    public void Codex_process_holding_two_rollouts_reports_both_so_the_identity_is_ambiguous()
    {
        Assert.SkipUnless(Probed, "reads /proc or Darwin process info");
        using var state = new TempStateDir();
        var owner = Path.Combine(state.Path, "rollout-2026-10-01T00-00-00-thread-owner.jsonl");
        var other = Path.Combine(state.Path, "rollout-2026-10-01T00-00-01-thread-other.jsonl");
        using var shell = Start("exec 3>>\"$1\" 4>>\"$2\"; exec sleep 30", owner, other);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            IReadOnlySet<string>? live = null;
            while (DateTime.UtcNow < deadline && live is not { Count: 2 })
            {
                live = LiveNativeSessions.Read(InteractiveAgentKind.Codex, shell.Id, null);
                Thread.Sleep(50);
            }
            Assert.Equal(["thread-other", "thread-owner"], live!.Order());
        }
        finally { OwnedProcessTermination.Kill(shell); }
    }

    static int? LiveChild(int parent)
    {
        if (OperatingSystem.IsMacOS())
        {
            return DarwinProcess.Table().Where(entry => entry.ParentPid == parent && !entry.IsZombie).Select(entry => (int?)entry.Pid).FirstOrDefault();
        }
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) { continue; }
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                if (stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[1] == parent.ToString()) { return pid; }
            }
            catch (IOException) { }
        }
        return null;
    }
}
