using AgentTeamForge.Business.Features.Jobs;

namespace AgentTeamForge.Tests.Features.WebConsole;

public sealed class WebConsoleLightTests
{
    [Theory]
    [InlineData("queued", "yellow")]
    [InlineData("running", "green")]
    [InlineData("working", "green")]
    [InlineData("completed", "grey")]
    [InlineData("succeeded", "grey")]
    [InlineData("idle", "green")]
    [InlineData("done", "grey")]
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
