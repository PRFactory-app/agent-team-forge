using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Setup;

public sealed class DaemonStartTests
{
    [Fact]
    public void LaunchdStartsTheDaemonOnlyWhenTheLoadedAgentRunsThisBinaryAndState()
    {
        using var temp = new TempStateDir();
        var home = temp.File("home");
        var path = LoginAutostart.FilePath(home, "macos");
        var loaded = true;
        (int, string) Launchd(string tool, IReadOnlyList<string> args) => (tool, args[0]) switch
        {
            ("id", _) => (0, "501\n"),
            ("launchctl", "print") => loaded ? (0, LaunchdOwnershipTests.Print("/opt/atf", "/srv/state")) : (113, "Could not find service"),
            _ => (1, "unexpected"),
        };

        Assert.Null(LoginAutostart.LaunchdService(home, "/opt/atf", "/srv/state", Launchd));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, LoginAutostart.MacPlist("/opt/atf", "/srv/state", "/usr/bin"));
        Assert.Equal("gui/501/com.agentteamforge.daemon", LoginAutostart.LaunchdService(home, "/opt/atf", "/srv/state", Launchd));
        Assert.Null(LoginAutostart.LaunchdService(home, "/opt/other/atf", "/srv/state", Launchd));
        Assert.Null(LoginAutostart.LaunchdService(home, "/opt/atf", "/srv/other-state", Launchd));

        // A plist that is not loaded (written, never bootstrapped) falls back to the direct launch.
        loaded = false;
        Assert.Null(LoginAutostart.LaunchdService(home, "/opt/atf", "/srv/state", Launchd));
    }

    [Fact]
    public void ClientCommandsRunWithTheBridgeHealthCheckMarker()
    {
        var (exitCode, output) = OperatingSystem.IsWindows()
            ? SetupCommand.RunCommand("powershell.exe", ["-NoProfile", "-Command", "$env:" + JobsMcpBridge.HealthCheckVariable])
            : SetupCommand.RunCommand("sh", ["-c", "printf %s \"$" + JobsMcpBridge.HealthCheckVariable + "\""]);

        Assert.Equal(0, exitCode);
        Assert.Equal("1", output.Trim());
    }
}
