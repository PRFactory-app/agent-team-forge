using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Hosting;

public sealed class StateDirectoryPathTests
{
    const string Socket = "/daemon.sock";

    // A directory whose daemon socket path is exactly `bytes` UTF-8 bytes long, padded mostly with `pad`.
    static string StateDirOfSocketBytes(TempStateDir temp, int bytes, char pad)
    {
        var prefix = temp.Path + "/";
        var room = bytes - System.Text.Encoding.UTF8.GetByteCount(prefix + Socket);
        var width = System.Text.Encoding.UTF8.GetByteCount(pad.ToString());
        return prefix + new string(pad, room / width) + new string('x', room % width);
    }

    [Fact]
    public void SocketLimitIsThePlatformsSunPathInBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var temp = new TempStateDir();
        var limit = StateDirectory.MaxSocketPathBytes;
        Assert.Equal(OperatingSystem.IsLinux() ? 107 : 103, limit);

        Assert.Null(StateDirectory.SocketPathProblem(StateDirOfSocketBytes(temp, limit, 'x')));
        Assert.NotNull(StateDirectory.SocketPathProblem(StateDirOfSocketBytes(temp, limit + 1, 'x')));
        // Fewer characters than the limit, more bytes: the kernel counts bytes.
        var wide = StateDirOfSocketBytes(temp, limit + 1, 'é');
        Assert.True((wide + Socket).Length <= limit);
        Assert.NotNull(StateDirectory.SocketPathProblem(wide));
    }

    [Fact]
    public void TooLongStateIsRefusedBeforeAnythingIsCreated()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var temp = new TempStateDir();
        var dir = temp.File(new string('x', 120));

        Assert.Equal(78, InitCommand.Run(dir, testProfile: true, queueLimit: null, maxRuntimeSeconds: null));
        Assert.False(Directory.Exists(dir));
        var options = new Dictionary<string, string> { ["mode"] = "headless", ["state-dir"] = dir, ["force"] = "true" };
        Assert.Equal(78, SetupCommand.Run(options, (_, _) => (127, ""), "/tmp/atf", homePath: temp.File("home")));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void CheckReportsTooLongStateWithoutFailingHarderOnALeftoverDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var temp = new TempStateDir();
        var dir = temp.File(new string('x', 120));
        var options = new Dictionary<string, string> { ["state-dir"] = dir, ["check"] = "true" };

        Assert.Equal(1, SetupCommand.Run(options, (_, _) => (127, ""), "/tmp/atf", homePath: temp.File("home")));
        // An earlier release created the directory before refusing it.
        StateDirectory.CreatePrivateDirectory(dir);
        Assert.Equal(1, SetupCommand.Run(options, (_, _) => (127, ""), "/tmp/atf", homePath: temp.File("home")));
    }
}
