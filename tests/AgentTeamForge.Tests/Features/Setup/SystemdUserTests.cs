using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Setup;

public sealed class SystemdUserTests
{
    [Fact]
    public void ServicePrefix_IsAnOwnKillModeProcessCollectedServiceWithAUniqueUnitAndNoSecrets()
    {
        var first = SystemdUser.ServicePrefix("/state", SystemdUser.NewUnitName(), "/state/daemon.log");
        var second = SystemdUser.ServicePrefix("/state", SystemdUser.NewUnitName(), "/state/daemon.log");

        Assert.Contains("--collect", first);
        Assert.DoesNotContain("--scope", first);
        Assert.Contains("KillMode=process", first);
        Assert.Contains("Restart=no", first);
        Assert.Equal("--", first[^1]);
        Assert.Contains("EnvironmentFile=-/state/daemon.env", first);
        Assert.DoesNotContain(first, a => a is "-E" or "--setenv" || a.StartsWith("--setenv=", StringComparison.Ordinal));
        var unit = Assert.Single(first, a => a.StartsWith("--unit=agentteamforge-daemon-", StringComparison.Ordinal));
        Assert.NotEqual(unit, Assert.Single(second, a => a.StartsWith("--unit=", StringComparison.Ordinal)));
    }

    [Fact]
    public void EnvironmentFile_QuotesValuesAndSkipsInvalidNames()
    {
        var content = SystemdUser.EnvironmentFileContent(new Dictionary<string, string?>
        {
            ["KEY"] = "a \"b\" \\ c\nd",
            ["BAD-NAME"] = "x",
            ["1BAD"] = "x",
            ["NULL"] = null,
        });

        Assert.Equal("KEY=\"a \\\"b\\\" \\\\ c\\nd\"\n", content);
    }

    [Fact]
    public void EnvironmentFile_IsOwnerOnly()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using var temp = new TempStateDir();
        var path = temp.File("daemon.env");

        SystemdUser.WriteEnvironmentFile(path, new Dictionary<string, string?> { ["TOKEN"] = "secret" });

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void LastExitResult_ReadsTheRecordedExitAndFallsBackToTheUnit()
    {
        using var temp = new TempStateDir();
        Assert.Null(SystemdUser.LastExitResult(temp.Path, _ => "ignored"));

        File.WriteAllText(Path.Combine(temp.Path, SystemdUser.UnitFile), "agentteamforge-daemon-abcd1234");
        Assert.Equal("Result=signal", SystemdUser.LastExitResult(temp.Path, unit => unit == "agentteamforge-daemon-abcd1234" ? "Result=signal" : null));

        File.WriteAllText(Path.Combine(temp.Path, SystemdUser.ExitFile), "result=signal code=killed status=KILL\n");
        Assert.Equal("result=signal code=killed status=KILL", SystemdUser.LastExitResult(temp.Path, _ => "ignored"));
    }

    [Fact]
    public void ServiceAvailable_NeedsSystemdRunAndAUserManager()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using var temp = new TempStateDir();
        var runtime = temp.File("run");
        Directory.CreateDirectory(Path.Combine(runtime, "systemd"));
        File.WriteAllText(Path.Combine(runtime, "bus"), "");
        string? Env(string name) => name == "XDG_RUNTIME_DIR" ? runtime : null;

        Assert.False(SystemdUser.ServiceAvailable(Env, _ => null));
        Assert.False(SystemdUser.ServiceAvailable(_ => null, _ => "/usr/bin/systemd-run"));
        Assert.True(SystemdUser.ServiceAvailable(Env, _ => "/usr/bin/systemd-run"));
    }

    [Fact]
    public void EnsureRuntimeDir_FillsOnlyAMissingValueFromALiveUserManager()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using var temp = new TempStateDir();
        var runtime = temp.File("run");
        var env = new Dictionary<string, string?>();

        SystemdUser.EnsureRuntimeDir(env, runtime);
        Assert.False(env.ContainsKey("XDG_RUNTIME_DIR"));

        Directory.CreateDirectory(Path.Combine(runtime, "systemd"));
        File.WriteAllText(Path.Combine(runtime, "bus"), "");
        SystemdUser.EnsureRuntimeDir(env, runtime);
        Assert.Equal(runtime, env["XDG_RUNTIME_DIR"]);

        env["XDG_RUNTIME_DIR"] = "/elsewhere";
        SystemdUser.EnsureRuntimeDir(env, runtime);
        Assert.Equal("/elsewhere", env["XDG_RUNTIME_DIR"]);

        env["XDG_RUNTIME_DIR"] = "";
        SystemdUser.EnsureRuntimeDir(env, runtime);
        Assert.Equal(runtime, env["XDG_RUNTIME_DIR"]);
    }

    [Fact]
    public void ParseSessionEnvironment_KeepsOnlySessionKeysWithPlainValues()
    {
        var parsed = SystemdUser.ParseSessionEnvironment(
            "WAYLAND_DISPLAY=wayland-1\nDISPLAY=:0\nXDG_RUNTIME_DIR=/run/user/1000\n" +
            "DBUS_SESSION_BUS_ADDRESS=$'unix:path=/x'\nPATH=/usr/bin\nHOME=/home/u\n");

        Assert.Equal(
            new Dictionary<string, string> { ["WAYLAND_DISPLAY"] = "wayland-1", ["DISPLAY"] = ":0", ["XDG_RUNTIME_DIR"] = "/run/user/1000" },
            parsed);
    }
}
