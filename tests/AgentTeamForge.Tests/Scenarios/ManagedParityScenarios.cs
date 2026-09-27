using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Scenarios;

[Trait("Category", "Scenario")]
public sealed class ManagedParityScenarios
{
    [Fact]
    public async Task Mcp_defers_then_interrupts_and_revives_without_duplicate_turns()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        await rig.StartDaemonAsync();
        var (_, client) = await rig.StartBridgeAsync();
        var capabilities = await SpikeRig.CallAsync(client, "list_backends", []);
        Assert.True(capabilities.BackendAvailability!["fake"]);
        string[] outputs = ["report.md"];
        var first = await SpikeRig.CallAsync(client, "submit_job", new()
        {
            ["backend"] = "fake",
            ["idempotency_key"] = "first",
            ["instruction"] = "first",
            ["hold"] = true,
            ["expected_outputs"] = outputs
        });
        var parentId = first.Job!.JobId;
        await rig.WaitForAckAsync(parentId);
        var args = new Dictionary<string, object?> { ["job_id"] = parentId, ["instruction"] = "second", ["idempotency_key"] = "next" };
        var queued = await SpikeRig.CallAsync(client, "follow_up", args);
        Assert.True(queued.Ok, queued.Error);
        Assert.Equal(JobStatus.Queued, queued.Job!.Status);
        Assert.Equal(queued.Job.JobId, (await SpikeRig.CallAsync(client, "follow_up", args)).Job!.JobId);
        Assert.Equal(JobStatus.Running, (await rig.GetAsync(parentId)).Job!.Status);
        rig.Release(parentId);
        var second = await rig.WaitForStatusAsync(queued.Job.JobId, JobStatus.Completed);
        Assert.Equal(1, second.Job!.Attempts);
        Assert.Equal("acknowledged", second.Job.Delivery!.State);
        Assert.Equal("fake-session-" + parentId, second.Job.SessionId);
        Assert.Equal(outputs, (await SpikeRig.CallAsync(client, "get_job", new() { ["job_id"] = parentId })).Job!.ExpectedOutputs);

        var busy = await SpikeRig.CallAsync(client, "submit_job", new()
        {
            ["backend"] = "fake",
            ["idempotency_key"] = "busy",
            ["instruction"] = "busy",
            ["hold"] = true
        });
        await rig.WaitForAckAsync(busy.Job!.JobId);
        var interrupted = await SpikeRig.CallAsync(client, "interrupt_job", new() { ["job_id"] = busy.Job.JobId });
        Assert.Equal("interrupted", interrupted.Job!.ReasonCode);
        var revived = await SpikeRig.CallAsync(client, "revive_agent", new()
        {
            ["job_id"] = busy.Job.JobId,
            ["instruction"] = "continue",
            ["idempotency_key"] = "revive"
        });
        Assert.True(revived.Ok, revived.Error);
        var final = await rig.WaitForStatusAsync(revived.Job!.JobId, JobStatus.Completed);
        Assert.Equal("fake-session-" + busy.Job.JobId, final.Job!.SessionId);
        Assert.True((await SpikeRig.CallAsync(client, "get_job_activity", new() { ["job_id"] = final.Job.JobId })).Ok);
    }
}
