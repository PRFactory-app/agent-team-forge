using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Terminals;

public sealed class CodexPathsTests
{
    [Fact]
    public void WindowsKeyAsciiLowercasesDriveAndUncWithoutChangingUnicode()
    {
        Assert.Equal(@"c:\code\new dir", CodexPaths.WindowsKey(@"C:\Code\New Dir"));
        Assert.Equal(@"c:\", CodexPaths.WindowsKey(@"C:\"));
        Assert.Equal(@"\\server\share\ä", CodexPaths.WindowsKey(@"\\SERVER\Share\ä"));
        Assert.Equal(@"\\server\share\dir", CodexPaths.WindowsKey(@"\\?\UNC\SERVER\Share\Dir"));
        Assert.Equal(@"c:\code", CodexPaths.WindowsKey(@"\\?\C:\Code"));
    }

    [Fact]
    public void TrustKeyResolvesParentSymlinkAndTrailingSeparator()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var state = new TempStateDir();
        var real = Directory.CreateDirectory(Path.Combine(state.Path, "Real", "Child")).FullName;
        var alias = Path.Combine(state.Path, "alias");
        Directory.CreateSymbolicLink(alias, Path.Combine(state.Path, "Real"));
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "agent", Path.Combine(alias, "Child") + "/", null, null,
            Path.Combine(state.Path, "bootstrap"));

        Assert.Equal(real, CodexPaths.TrustKey(launch.WorkingDirectory));
        Assert.Contains("projects={'" + real + "'={trust_level='trusted'}}", InteractiveAgentCommand.Arguments(launch));
        Assert.Equal(Path.GetPathRoot(state.Path), CodexPaths.TrustKey(Path.GetPathRoot(state.Path)!));
    }

    [Fact]
    public void RelativeHomeIsAnchoredAndInteractiveCodexCmdIsRefused()
    {
        using var state = new TempStateDir();
        var env = new Dictionary<string, string?> { ["HOME"] = state.Path, ["CODEX_HOME"] = "profile/../isolated codex" };
        var home = CodexPaths.Home(name => env.GetValueOrDefault(name), state.Path);
        Assert.Equal(Path.Combine(state.Path, "isolated codex"), home);
        Assert.True(Path.IsPathFullyQualified(home));
        var refusal = Assert.Throws<BackendNotStartedException>(() =>
            WtTabControl.EnsureInteractiveCodexNative(InteractiveAgentKind.Codex, @"C:\tools\codex.cmd"));
        Assert.Contains("codex.exe", refusal.Message);
        WtTabControl.EnsureInteractiveCodexNative(InteractiveAgentKind.Claude, @"C:\tools\claude.cmd");
    }
}
