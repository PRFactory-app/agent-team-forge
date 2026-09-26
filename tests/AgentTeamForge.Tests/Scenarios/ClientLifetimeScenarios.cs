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
        Assert.Contains(tools, t => t.Name == "submit_job");
        Assert.Contains(tools, t => t.Name == "get_job");

        var submitted = await SpikeRig.CallAsync(client1, "submit_job",
            new() { ["backend"] = "fake", ["idempotency_key"] = "demo-1", ["instruction"] = "say hi ✓", ["hold"] = true });
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
            var r = await SpikeRig.CallAsync(client2, "get_job", new() { ["job_id"] = jobId });
            return r.Job?.Status == JobStatus.Completed ? r : null;
        }, "completed via fresh bridge");
        Assert.Equal("fake-result: say hi ✓", done.Job!.Result);

        var retry = await SpikeRig.CallAsync(client2, "submit_job",
            new() { ["backend"] = "fake", ["idempotency_key"] = "demo-1", ["instruction"] = "say hi ✓", ["hold"] = true });
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
    public async Task Mcp_follow_up_resumes_the_native_session_of_the_finished_job()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        await rig.StartDaemonAsync();
        var (_, client) = await rig.StartBridgeAsync();

        var first = await SpikeRig.CallAsync(client, "submit_job",
            new() { ["backend"] = "fake", ["idempotency_key"] = "turn-1", ["instruction"] = "first", ["cwd"] = rig.StateDir });
        var parent = await rig.WaitForStatusAsync(first.Job!.JobId, JobStatus.Completed);
        Assert.Equal("fake-session-" + parent.Job!.JobId, parent.Job.SessionId);

        var next = await SpikeRig.CallAsync(client, "follow_up",
            new() { ["job_id"] = parent.Job.JobId, ["idempotency_key"] = "turn-2", ["instruction"] = "second" });
        Assert.Equal("accepted", next.Outcome);
        var child = await rig.WaitForStatusAsync(next.Job!.JobId, JobStatus.Completed);

        // The fake child echoes the session it was asked to resume.
        Assert.Equal(parent.Job.SessionId, child.Job!.SessionId);
        Assert.Equal((parent.Job.JobId, rig.StateDir, "fake-result: second"), (child.Job.ParentJobId, child.Job.Cwd, child.Job.Result));
        var listed = await SpikeRig.CallAsync(client, "list_jobs", []);
        Assert.Equal([child.Job.JobId, parent.Job.JobId], listed.Page!.Jobs.Select(j => j.JobId));
    }

    [Fact]
    public async Task Mcp_stop_cancels_a_running_child_and_its_session_can_be_resumed()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        await rig.StartDaemonAsync();
        var (_, client) = await rig.StartBridgeAsync();
        Assert.Contains(await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken), t => t.Name == "stop_job");

        var submitted = await SpikeRig.CallAsync(client, "submit_job",
            new() { ["backend"] = "fake", ["idempotency_key"] = "stop-parent", ["instruction"] = "hold", ["hold"] = true });
        var jobId = submitted.Job!.JobId;
        var running = await Bounded.Until(async () =>
        {
            var result = await rig.GetAsync(jobId);
            return result.Job?.SessionId is not null ? result : null;
        }, "running session");

        var stopped = await SpikeRig.CallAsync(client, "stop_job", new() { ["job_id"] = jobId });
        Assert.Equal(JobStatus.Cancelled, stopped.Job!.Status);
        Assert.Equal(JobStatus.Cancelled, (await rig.GetAsync(jobId)).Job!.Status);

        var followUp = await SpikeRig.CallAsync(client, "follow_up",
            new() { ["job_id"] = jobId, ["idempotency_key"] = "stop-child", ["instruction"] = "continue" });
        var child = await rig.WaitForStatusAsync(followUp.Job!.JobId, JobStatus.Completed);
        Assert.Equal(running.Job!.SessionId, child.Job!.SessionId);
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
