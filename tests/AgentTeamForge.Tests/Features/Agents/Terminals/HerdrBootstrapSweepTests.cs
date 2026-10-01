using System.Diagnostics;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Terminals;

public sealed class HerdrBootstrapSweepTests
{
    const int ServerPid = 4100, ShellPid = 4200, OtherPid = 4300;

    [Fact]
    public void Recorded_bootstrap_goes_only_when_its_recorded_server_or_shell_is_gone()
    {
        using var state = new TempStateDir();
        var runner = new Runner { Identities = { [ServerPid] = 7, [ShellPid] = 8 } };
        var live = Recorded(state, "atf00000000000000000001", ServerPid, ShellPid);
        var deadServer = DeadPid();
        var gone = Recorded(state, "atf00000000000000000002", deadServer, ShellPid);
        var replacedShell = Recorded(state, "atf00000000000000000003", ServerPid, ShellPid, shellTicks: 99);
        var unreadable = Bootstrap(state, "atf00000000000000000004");
        File.WriteAllText(Path.ChangeExtension(unreadable, ".owned.json"), "{not json");

        Assert.Equal(2, HerdrOwnedSessions.SweepStaleBootstraps(state.Path, runner, () => [], _ => { }));

        Assert.True(File.Exists(live) && File.Exists(HerdrTerminal.ShellProofPath(live)));
        Assert.True(File.Exists(unreadable));
        foreach (var bootstrap in new[] { gone, replacedShell })
        {
            Assert.False(File.Exists(bootstrap));
            Assert.False(File.Exists(HerdrTerminal.ShellProofPath(bootstrap)));
            // The record still fences its job until stop_agent forgets it.
            Assert.True(File.Exists(Path.ChangeExtension(bootstrap, ".owned.json")));
        }
    }

    [Fact]
    public void Unrecorded_bootstrap_goes_only_when_no_live_process_carries_it()
    {
        using var state = new TempStateDir();
        var carried = Bootstrap(state, "atf00000000000000000001");
        var stale = Bootstrap(state, "atf00000000000000000002");
        var runner = new Runner { Environment = { [OtherPid] = carried } };

        Assert.Equal(0, HerdrOwnedSessions.SweepStaleBootstraps(state.Path, runner, () => null, _ => { }));
        Assert.True(File.Exists(stale)); // An unreadable process list proves nothing.

        Assert.Equal(1, HerdrOwnedSessions.SweepStaleBootstraps(state.Path, runner, () => [OtherPid], _ => { }));
        Assert.True(File.Exists(carried));
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void Hidden_shell_environment_needs_the_reporting_shell_to_have_exited()
    {
        using var state = new TempStateDir();
        var exited = Bootstrap(state, "atf00000000000000000001", DeadPid());
        var running = Bootstrap(state, "atf00000000000000000002", ShellPid);
        var unreported = Bootstrap(state, "atf00000000000000000003");
        File.Delete(HerdrTerminal.ShellProofPath(unreported));
        var orphanProof = HerdrTerminal.ShellProofPath(state.File("herdr/atf00000000000000000004.bootstrap"));
        File.WriteAllText(orphanProof, "1");
        var runner = new Runner { HiddenEnvironment = true, Identities = { [ShellPid] = 8 } };

        Assert.Equal(1, HerdrOwnedSessions.SweepStaleBootstraps(state.Path, runner, () => [ShellPid], _ => { }));

        Assert.False(File.Exists(exited));
        Assert.False(File.Exists(HerdrTerminal.ShellProofPath(exited)));
        Assert.True(File.Exists(running));
        Assert.True(File.Exists(unreported));
        Assert.False(File.Exists(orphanProof));
    }

    static string Bootstrap(TempStateDir state, string agentName, int shellProof = ShellPid)
    {
        var path = state.File(Path.Combine("herdr", agentName + ".bootstrap"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, agentName);
        File.WriteAllText(HerdrTerminal.ShellProofPath(path), shellProof.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return path;
    }

    static string Recorded(TempStateDir state, string agentName, int serverPid, int shellPid, ulong shellTicks = 8)
    {
        var bootstrap = Bootstrap(state, agentName);
        HerdrOwnedSessions.Save(new InteractiveLaunch(InteractiveAgentKind.Codex, agentName, state.Path, null, null, bootstrap) { JobId = "job-" + agentName },
            new OwnedHerdrSession("atf-s", "/tmp/s.sock", serverPid, 7, "atf-owner-x", "w1") { ShellPid = shellPid, ShellStartTicks = shellTicks });
        return bootstrap;
    }

    /// <summary>A PID that certainly belonged to a process that has exited.</summary>
    static int DeadPid()
    {
        using var process = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", OperatingSystem.IsWindows() ? "/c exit" : "-c :")
        { UseShellExecute = false })!;
        process.WaitForExit();
        return process.Id;
    }

    sealed class Runner : IHerdrProcessRunner
    {
        public Dictionary<int, ulong> Identities { get; } = [];
        public Dictionary<int, string> Environment { get; } = [];
        public bool HiddenEnvironment { get; init; }

        public bool EnvironmentMayBeHidden => HiddenEnvironment;
        public ProcessIdentity? Identity(int pid) => Identities.TryGetValue(pid, out var ticks) ? new(pid, ticks) : null;
        public string? EnvironmentValue(int pid, string name) =>
            name == HerdrTerminal.BootstrapVariable && Environment.TryGetValue(pid, out var value) ? value : null;
        public int? ParentOf(int pid) => null;
        public IReadOnlyList<ProcessIdentity> FindServers(string sessionName, string? socketPath = null) => [];
        public Task<CapturedProcess> CaptureAsync(ProcessStartInfo psi, TimeSpan timeout, int maxStdoutBytes, int maxStderrBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task StartDetachedAsync(ProcessStartInfo psi, TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
