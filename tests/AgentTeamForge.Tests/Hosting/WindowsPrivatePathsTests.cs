using AgentTeamForge.Host.Hosting;

namespace AgentTeamForge.Tests.Hosting;

public sealed class WindowsPrivatePathsTests
{
    [Fact]
    public void ReopenedFileAclMustBelongOnlyToCurrentUser()
    {
        var user = new byte[] { 1, 2, 3 };
        var other = new byte[] { 4, 5, 6 };
        Assert.True(WindowsPrivatePaths.IsPrivateAcl(user, user, [(0, user)]));
        Assert.False(WindowsPrivatePaths.IsPrivateAcl(user, other, [(0, user)]));
        Assert.False(WindowsPrivatePaths.IsPrivateAcl(user, user, [(0, user), (0, other)]));
        Assert.False(WindowsPrivatePaths.IsPrivateAcl(user, user, [(1, user)]));
        Assert.False(WindowsPrivatePaths.IsPrivateAcl(user, user, []));
        // An elevated admin's files are owned by BUILTIN\Administrators; the DACL must still be user-only.
        Assert.True(WindowsPrivatePaths.IsPrivateAcl(user, WindowsPrivatePaths.Administrators, [(0, user)]));
        Assert.False(WindowsPrivatePaths.IsPrivateAcl(user, WindowsPrivatePaths.Administrators, [(0, WindowsPrivatePaths.Administrators)]));
    }
}
