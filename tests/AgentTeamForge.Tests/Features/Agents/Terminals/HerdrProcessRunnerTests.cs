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

    [Fact]
    public void UnreadableEnvironmentIsNeverAServer()
    {
        // Another user's /proc/PID/environ is EACCES; its plain `herdr server` must not count as our default.
        Assert.False(HerdrProcessRunner.IsServer(["/usr/bin/herdr", "server"], null, "default"));
        Assert.False(HerdrProcessRunner.IsServer(["/usr/bin/herdr", "--session", "demo", "server"], null, "demo"));
        Assert.True(HerdrProcessRunner.IsServer(["/usr/bin/herdr", "server"], ["HOME=/h"], "default"));
        Assert.True(HerdrProcessRunner.IsServer(["/usr/bin/herdr", "server"], ["HERDR_SESSION=demo"], "demo"));
    }
}
