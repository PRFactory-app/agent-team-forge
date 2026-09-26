using AgentTeamForge.Business.Features.Jobs;

namespace AgentTeamForge.Tests.Features.WebConsole;

public sealed class WebConsoleLightTests
{
    [Theory]
    [InlineData("queued", "yellow")]
    [InlineData("running", "yellow")]
    [InlineData("working", "yellow")]
    [InlineData("completed", "green")]
    [InlineData("succeeded", "green")]
    [InlineData("idle", "green")]
    [InlineData("done", "green")]
    [InlineData("failed", "red")]
    [InlineData("timed_out", "red")]
    [InlineData("needs_reconciliation", "red")]
    [InlineData("blocked", "red")]
    [InlineData("cancelled", "grey")]
    [InlineData("stopped", "grey")]
    public void Status_has_operator_light(string status, string expected)
    {
        var job = new JobSummary("job_1", status, null, 1, "", "");

        Assert.Equal(expected, job.Light);
    }
}
