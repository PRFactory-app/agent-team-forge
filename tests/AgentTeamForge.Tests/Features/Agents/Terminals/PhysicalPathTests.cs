using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Terminals;

public sealed class PhysicalPathTests
{
    [Fact]
    public void MissingTailStaysAsWrittenBehindAResolvedParent()
    {
        using var state = new TempStateDir();
        var root = PhysicalPath.Resolve(state.Path)!;
        var missing = Path.Combine(state.Path, "gone", "deeper");

        Assert.Equal(Path.Combine(root, "gone", "deeper"), PhysicalPath.Resolve(missing));
    }

    [Fact]
    public void MissingTailBehindALinkIsResolvedUpToTheLink()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var state = new TempStateDir();
        var root = PhysicalPath.Resolve(state.Path)!;
        Directory.CreateDirectory(Path.Combine(root, "real"));
        Directory.CreateSymbolicLink(Path.Combine(root, "alias"), Path.Combine(root, "real"));

        Assert.Equal(Path.Combine(root, "real", "gone"), PhysicalPath.Resolve(Path.Combine(state.Path, "alias", "gone")));
    }

    [Fact]
    public void LinkLoopHasNoPhysicalPath()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var state = new TempStateDir();
        Directory.CreateSymbolicLink(state.File("a"), state.File("b"));
        Directory.CreateSymbolicLink(state.File("b"), state.File("a"));

        Assert.Null(PhysicalPath.Resolve(Path.Combine(state.Path, "a", "x")));
    }
}
