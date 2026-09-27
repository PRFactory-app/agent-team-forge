namespace AgentTeamForge.Tests.Support;

public sealed class AgentEnvironmentFixtureTests
{
    [Theory]
    [InlineData("ATF_AGENT_NAME", true)]
    [InlineData("ATF_EXTERNAL_ONLY", true)]
    [InlineData("ATF_WAKE_KEY", true)]
    [InlineData("CODEX_THREAD_ID", true)]
    [InlineData("CLAUDE_CODE_SESSION_ID", true)]
    [InlineData("CLAUDE_CODE_MESSAGING_SOCKET", true)]
    [InlineData("CLAUDE_CODE_SSE_PORT", true)]
    [InlineData("ATF_TEST_TMP_ROOT", false)]
    [InlineData("ATF_KEEP_TMP", false)]
    [InlineData("ATF_REAL_CODEX", false)]
    [InlineData("ATF_HERDR_INTEGRATION", false)]
    [InlineData("CLAUDE_CONFIG_DIR", false)]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN", false)]
    public void Classifies_inherited_identity_wake_and_test_control_variables(string name, bool shouldClear) =>
        Assert.Equal(shouldClear, AgentEnvironmentFixture.ShouldClear(name));
}
