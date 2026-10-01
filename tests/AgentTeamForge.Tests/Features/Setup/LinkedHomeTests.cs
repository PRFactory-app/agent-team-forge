using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Setup;

/// <summary>
/// HOME reached through a symbolic link (macOS HOME=/tmp/x is /private/tmp/x), while the OS
/// reports the running executable fully resolved.
/// </summary>
public sealed class LinkedHomeTests
{
    sealed record Install(string Home, string Release, string Stable, string State);

    // Paths inside the returned Home go through the link; Release and State are the resolved spellings.
    static Install Installed(TempStateDir temp)
    {
        var real = temp.File("real-home");
        var home = temp.File("home");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(home, real);
        var root = Path.Combine(real, ".local", "share", "agentteamforge");
        var release = Path.Combine(root, "releases", "0.2.0");
        Directory.CreateDirectory(release);
        File.WriteAllText(Path.Combine(release, "atf"), "binary");
        Directory.CreateSymbolicLink(Path.Combine(root, "current"), "releases/0.2.0");
        var stable = Path.Combine(home, ".local", "bin", "atf");
        Directory.CreateDirectory(Path.GetDirectoryName(stable)!);
        // install.sh writes an absolute link built from HOME as given.
        File.CreateSymbolicLink(stable, Path.Combine(home, ".local", "share", "agentteamforge", "current", "atf"));
        return new(home, Path.Combine(release, "atf"), stable, Path.Combine(real, ".local", "state", "agentteamforge"));
    }

    [Fact]
    public void StableBinaryIsRegisteredWhenTheExecutableIsReportedResolved()
    {
        using var temp = new TempStateDir();
        var install = Installed(temp);

        Assert.Equal(install.Stable, ClientSetup.StableBinary(install.Release, install.Home));
        Assert.Equal(install.Stable, ClientSetup.StableBinary(install.Stable, install.Home));
    }

    [Fact]
    public void StableBinaryKeepsAnUnrelatedExecutable()
    {
        using var temp = new TempStateDir();
        var install = Installed(temp);
        var other = temp.File("other/atf");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "binary");

        Assert.Equal(other, ClientSetup.StableBinary(other, install.Home));
    }

    [Fact]
    public void CurrentReleaseMapsAResolvedOldReleaseToCurrent()
    {
        using var temp = new TempStateDir();
        var install = Installed(temp);
        var old = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(install.Release)!)!, "0.1.0", "atf");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, "binary");

        Assert.Equal(Path.Combine(install.Home, ".local", "share", "agentteamforge", "releases", "0.2.0", "atf"),
            ClientSetup.CurrentRelease(old, install.Home));
    }

    [Fact]
    public void DefaultStateAndClientHomesAreRecognisedThroughTheLink()
    {
        using var temp = new TempStateDir();
        var install = Installed(temp);
        var values = new Dictionary<string, string?>
        {
            ["CODEX_HOME"] = Path.Combine(temp.File("real-home"), ".codex"),
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(install.Home, ".claude"),
        };

        Assert.Null(SetupCommand.ClientConfigProblem(install.State, install.Home, force: false, values.GetValueOrDefault));
        Assert.NotNull(SetupCommand.ClientConfigProblem(temp.File("elsewhere"), install.Home, force: false, values.GetValueOrDefault));
    }

    [Fact]
    public void TeardownRemovesARegistrationWrittenWithResolvedPaths()
    {
        using var temp = new TempStateDir();
        var install = Installed(temp);
        var asWritten = Path.Combine(install.Home, ".local", "state", "agentteamforge");
        var removed = new List<string>();
        (int, string) Runner(string tool, IReadOnlyList<string> args)
        {
            if (args.SequenceEqual(["mcp", "get", "agentteamforge"]))
            {
                return tool == "claude"
                    ? (0, $"Scope: User config\nCommand: {install.Release}\nArgs: mcp --state-dir {install.State}\n")
                    : (0, $"Command: {temp.File("other/atf")}\nArgs: mcp --state-dir {install.State}\n");
            }
            removed.Add(tool + " " + string.Join(' ', args));
            return (0, "");
        }

        Assert.True(ClientSetup.Teardown(install.Stable, asWritten, install.Home, Runner));
        Assert.Equal(["claude mcp remove agentteamforge --scope user"], removed);
    }
}
