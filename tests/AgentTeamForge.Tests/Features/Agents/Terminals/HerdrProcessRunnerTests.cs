using AgentTeamForge.Business.Features.Agents.Terminals;

namespace AgentTeamForge.Tests.Features.Agents.Terminals;

public class HerdrProcessRunnerTests
{
    [Theory]
    [InlineData(null, "default", true)]
    [InlineData("default", "default", true)]
    [InlineData("demo", "default", false)]
    [InlineData("demo", "demo", true)]
    [InlineData(null, "demo", false)]
    public void PlainServerUsesEnvironmentSession(string? environmentSession, string requestedSession, bool expected)
    {
        Assert.Equal(expected, HerdrProcessRunner.ServerArgv(["/usr/bin/herdr", "server"], requestedSession, environmentSession));
    }

    [Fact]
    public void ExplicitSessionArgvStillMatches()
    {
        Assert.True(HerdrProcessRunner.ServerArgv(["/usr/bin/herdr", "--session", "demo", "server"], "demo", null));
        Assert.False(HerdrProcessRunner.ServerArgv(["/usr/bin/herdr", "--session", "demo", "server"], "default", "default"));
        Assert.False(HerdrProcessRunner.ServerArgv(["/usr/bin/other", "server"], "demo", "demo"));
    }
}
