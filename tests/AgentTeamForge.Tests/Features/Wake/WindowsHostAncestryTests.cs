using AgentTeamForge.Host.Features.Wake;

namespace AgentTeamForge.Tests.Features.Wake;

public sealed class WindowsHostAncestryTests
{
    [Fact]
    public void ResolvesNearestHostThroughWindowsShimChain()
    {
        var rows = new Dictionary<int, WindowsHostAncestry.Row>
        {
            [10] = new(11, "atf.exe"),
            [11] = new(12, "cmd.exe"),
            [12] = new(13, "node.exe", @"node C:\Users\me\node_modules\@earendil-works\pi-coding-agent\dist\cli.js"),
            [13] = new(1, "claude.exe"),
        };
        Assert.Equal((12, "pi"), WindowsHostAncestry.Resolve(10, rows));
    }

    [Fact]
    public void UnknownNodeAndCycleDoNotClaimAWakeHost()
    {
        var rows = new Dictionary<int, WindowsHostAncestry.Row>
        {
            [10] = new(11, "atf.exe"),
            [11] = new(10, "node.exe", "node arbitrary.js"),
        };
        Assert.Null(WindowsHostAncestry.Resolve(10, rows));
    }
}
