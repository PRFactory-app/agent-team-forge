using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

/// <summary>A3: the startup self-test decides the effective mode per CLI; a failed probe downgrades, a passed one adds nothing.</summary>
public sealed class SandboxSelfTestTests
{
    static string? Installed(string name) => "/usr/bin/" + name;

    /// <summary>A sandbox that runs the probe: the workspace write lands; the outside write and secret reads are blocked unless told otherwise.</summary>
    static SandboxSelfTest.Runner Sandbox(bool leaksWrite = false, bool runs = true) => (_, _, work, _) =>
    {
        if (!runs) { return (1, "bwrap: setting up uid map: Permission denied"); }
        File.WriteAllText(Path.Combine(work, "inside"), "in");
        return (0, (leaksWrite ? "ATF-PROBE-OUTSIDE-WRITTEN\n" : "") + "ATF-PROBE-DONE\n");
    };

    [Fact]
    public void Passing_probes_add_no_downgrade()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using var home = new TempStateDir();
        var test = SandboxSelfTest.Run(Installed, Sandbox(), home.Path);

        Assert.Null(test.Reason(BackendCatalog.Codex));
        Assert.Null(test.Reason(BackendCatalog.Claude));
        Assert.Null(test.Probe((_, _, _) => null)(BackendCatalog.Codex, null, null));
    }

    [Fact]
    public void A_sandbox_that_lets_a_write_out_or_does_not_run_downgrades_its_cli()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using var home = new TempStateDir();
        Assert.Equal(SafetyDowngrade.Unavailable, SandboxSelfTest.Run(Installed, Sandbox(leaksWrite: true), home.Path).Reason(BackendCatalog.Codex));

        var broken = SandboxSelfTest.Run(Installed, Sandbox(runs: false), home.Path);
        Assert.Equal(SafetyDowngrade.Unavailable, broken.Reason(BackendCatalog.Claude));
        Assert.Equal(SafetyDowngrade.Unavailable, broken.Probe((_, _, _) => null)(BackendCatalog.Claude, null, null));
        Assert.Contains(broken.Report(), line => line.StartsWith("claude: sandbox_unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void Claude_without_bubblewrap_or_socat_is_downgraded_and_an_absent_cli_is_not_probed()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using var home = new TempStateDir();
        var noSocat = SandboxSelfTest.Run(name => name == "socat" ? null : Installed(name), Sandbox(), home.Path);
        Assert.Equal(SafetyDowngrade.Unavailable, noSocat.Reason(BackendCatalog.Claude));
        Assert.Null(noSocat.Reason(BackendCatalog.Codex));

        var noCodex = SandboxSelfTest.Run(name => name == "codex" ? null : Installed(name), Sandbox(), home.Path);
        Assert.Null(noCodex.Reason(BackendCatalog.Codex));
        Assert.DoesNotContain(noCodex.Report(), line => line.StartsWith("codex", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_descendant_that_keeps_the_pipe_open_times_out_and_is_ended_with_the_group()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using var dir = new TempStateDir();
        var pidFile = Path.Combine(dir.Path, "child.pid");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = SandboxSelfTest.RunProcess("sh", ["-c", $"sleep 60 & echo $! > '{pidFile}'; exit 0"], dir.Path, TimeSpan.FromSeconds(2));

        Assert.Null(result);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
        var child = File.ReadAllText(pidFile).Trim();
        await Bounded.Until(() => !Directory.Exists("/proc/" + child) || File.ReadAllText($"/proc/{child}/stat").Contains(") Z ", StringComparison.Ordinal),
            "descendant ended");
    }
}
