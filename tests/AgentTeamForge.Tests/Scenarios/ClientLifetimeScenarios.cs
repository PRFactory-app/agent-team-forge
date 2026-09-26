using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Scenarios;

/// <summary>
/// Principal demo path: MCP bridge submits → bridge killed abruptly while the
/// fake run is active → fresh bridge retrieves the committed result → same-key
/// retry resolves to the same job with no second execution.
/// </summary>
[Trait("Category", "Scenario")]
public sealed class ClientLifetimeScenarios
{
    [Fact]
    public async Task Killed_bridge_does_not_stop_work_and_a_fresh_bridge_gets_the_result()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var daemon = await rig.StartDaemonAsync();

        var (bridge1, client1) = await rig.StartBridgeAsync();
        var tools = await client1.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, t => t.Name == "job_submit");
        Assert.Contains(tools, t => t.Name == "job_get");

        var submitted = await SpikeRig.CallAsync(client1, "job_submit",
            new() { ["idempotency_key"] = "demo-1", ["instruction"] = "say hi ✓", ["hold"] = true });
        Assert.True(submitted.Ok);
        Assert.Equal("accepted", submitted.Outcome);
        var jobId = submitted.Job!.JobId;
        await rig.WaitForAckAsync(jobId);

        OwnedProcesses.KillAbruptly(bridge1);
        Assert.False(daemon.HasExited);
        Assert.Equal(JobStatus.Running, (await rig.GetAsync(jobId)).Job!.Status);

        rig.Release(jobId);
        var (_, client2) = await rig.StartBridgeAsync();
        var done = await Bounded.Until(async () =>
        {
            var r = await SpikeRig.CallAsync(client2, "job_get", new() { ["job_id"] = jobId });
            return r.Job?.Status == JobStatus.Completed ? r : null;
        }, "completed via fresh bridge");
        Assert.Equal("fake-result: say hi ✓", done.Job!.Result);

        var retry = await SpikeRig.CallAsync(client2, "job_submit",
            new() { ["idempotency_key"] = "demo-1", ["instruction"] = "say hi ✓", ["hold"] = true });
        Assert.Equal("existing", retry.Outcome);
        Assert.Equal(jobId, retry.Job!.JobId);
        Assert.Equal(1, rig.Invocations(jobId));
        Assert.Equal(1, retry.Job.Attempts);

        // Stored completion survives an abrupt daemon kill; the kernel lock allows restart.
        OwnedProcesses.KillAbruptly(daemon);
        await rig.StartDaemonAsync();
        var afterRestart = await rig.GetAsync(jobId);
        Assert.Equal(JobStatus.Completed, afterRestart.Job!.Status);
        Assert.Equal(1, rig.Invocations(jobId));
    }

    [Fact]
    public async Task Queued_work_runs_with_no_connected_client()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        await rig.StartDaemonAsync();

        // The CLI client process exits right after acceptance; nothing stays connected.
        var accepted = await rig.SubmitAsync("no-client", "background", hold: true);
        Assert.Equal("accepted", accepted.Outcome);
        await rig.WaitForAckAsync(accepted.Job!.JobId);
        rig.Release(accepted.Job.JobId);

        var done = await rig.WaitForStatusAsync(accepted.Job.JobId, JobStatus.Completed);
        Assert.Equal("fake-result: background", done.Job!.Result);
    }
}
