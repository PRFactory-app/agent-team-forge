using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Setup;

public sealed class SystemdUserTests
{
    [Fact]
    public void ScopePrefix_IsAnOwnKillModeProcessScopeWithAUniqueUnit()
    {
        var first = SystemdUser.ScopePrefix("/state");
        var second = SystemdUser.ScopePrefix("/state");

        Assert.Equal(["--user", "--scope", "--collect"], first.Where(a => a is "--user" or "--scope" or "--collect"));
        Assert.Contains("KillMode=process", first);
        Assert.Equal("--", first[^1]);
        var unit = Assert.Single(first, a => a.StartsWith("--unit=agentteamforge-daemon-", StringComparison.Ordinal));
        Assert.NotEqual(unit, Assert.Single(second, a => a.StartsWith("--unit=", StringComparison.Ordinal)));
    }

    [Fact]
    public void ScopeAvailable_NeedsSystemdRunAndAUserManager()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using var temp = new TempStateDir();
        var runtime = temp.File("run");
        Directory.CreateDirectory(Path.Combine(runtime, "systemd"));
        File.WriteAllText(Path.Combine(runtime, "bus"), "");
        string? Env(string name) => name == "XDG_RUNTIME_DIR" ? runtime : null;

        Assert.False(SystemdUser.ScopeAvailable(Env, _ => null));
        Assert.False(SystemdUser.ScopeAvailable(_ => null, _ => "/usr/bin/systemd-run"));
        Assert.True(SystemdUser.ScopeAvailable(Env, _ => "/usr/bin/systemd-run"));
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
