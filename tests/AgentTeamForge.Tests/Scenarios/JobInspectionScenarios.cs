using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Scenarios;

/// <summary>
/// Read-only job_list over the real daemon IPC path (CLI and MCP bridge):
/// bounded pages, honest committed state, and no effect on dispatch.
/// </summary>
[Trait("Category", "Scenario")]
public sealed class JobInspectionScenarios
{
    [Fact]
    public async Task Operator_lists_jobs_by_page_and_status_without_knowing_ids()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        await rig.StartDaemonAsync();

        // One dispatcher: finish one job, then hold a second one running.
        var done = await rig.SubmitAsync("done", "quick");
        await rig.WaitForStatusAsync(done.Job!.JobId, JobStatus.Completed);
        var held = await rig.SubmitAsync("held", "wait", hold: true);
        await rig.WaitForAckAsync(held.Job!.JobId);

        var first = await rig.ClientAsync("list", "--limit", "1");
        Assert.True(first.Ok);
        var running = Assert.Single(first.Page!.Jobs);
        Assert.Equal(held.Job.JobId, running.JobId);
        Assert.Equal(JobStatus.Running, running.Status);
        Assert.True(first.Page.HasMore);
        var second = await rig.ClientAsync("list", "--limit", "1", "--cursor", first.Page.NextCursor!);
        Assert.Equal([done.Job.JobId], second.Page!.Jobs.Select(j => j.JobId));
        Assert.False(second.Page.HasMore);

        var completed = await rig.ClientAsync("list", "--status", JobStatus.Completed);
        Assert.Equal([done.Job.JobId], completed.Page!.Jobs.Select(j => j.JobId));
        Assert.Equal("invalid_request", (await rig.ClientAsync("list", "--limit", "500")).Error);
        Assert.Equal("invalid_request", (await rig.ClientAsync("list", "--limit", "many")).Error);

        var (_, bridge) = await rig.StartBridgeAsync();
        var tools = await bridge.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, t => t.Name == "job_list");
        var viaMcp = await SpikeRig.CallAsync(bridge, "job_list", new() { ["status"] = JobStatus.Running, ["limit"] = 5 });
        Assert.Equal([held.Job.JobId], viaMcp.Page!.Jobs.Select(j => j.JobId));

        // Present but wrongly typed (or null) string fields must be rejected, not treated as absent.
        foreach (var malformed in new Dictionary<string, object?>[]
        {
            new() { ["status"] = 5 },
            new() { ["status"] = null },
            new() { ["status"] = new[] { JobStatus.Running } },
            new() { ["cursor"] = true },
            new() { ["cursor"] = null },
            new() { ["cursor"] = 12345 },
            new() { ["limit"] = "5" },
            new() { ["limit"] = null },
        })
        {
            var rejected = await SpikeRig.CallAsync(bridge, "job_list", malformed);
            Assert.False(rejected.Ok);
            Assert.Equal("invalid_request", rejected.Error);
            Assert.Null(rejected.Page);
        }

        // Listing never dispatched or re-ran anything.
        Assert.Equal(1, rig.Invocations(held.Job.JobId));
        Assert.Equal(1, rig.Invocations(done.Job.JobId));
        rig.Release(held.Job.JobId);
        await rig.WaitForStatusAsync(held.Job.JobId, JobStatus.Completed);
        Assert.Equal(1, rig.Invocations(held.Job.JobId));
    }
}

/// <summary>
/// Scoped to this fixture: a 64-char reason code covers the short fixed codes the current fake
/// dispatcher writes. Schema does not bound reason_code and outgoing frames have no cap, so this
/// is not a general response-size guarantee.
/// </summary>
public sealed class JobListFrameBoundTests
{
    [Fact]
    public void A_full_page_fits_one_ipc_frame()
    {
        var wide = new AgentTeamForge.Business.Features.Jobs.JobSummary(
            "job_" + new string('f', 32), JobStatus.NeedsReconciliation, new string('r', 64), int.MaxValue,
            DateTimeOffset.MaxValue.ToString("O"), DateTimeOffset.MaxValue.ToString("O"));
        var page = new AgentTeamForge.Business.Features.Jobs.JobListPage(
            [.. Enumerable.Repeat(wide, AgentTeamForge.Business.Features.Jobs.ListJobs.MaxPageSize)],
            AgentTeamForge.Business.Features.Jobs.ListJobs.MaxPageSize, true, wide.JobId);

        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new AgentTeamForge.Host.Transport.IpcResponse(true, Outcome: "listed", Page: page),
            AgentTeamForge.Host.Transport.IpcJson.Default.IpcResponse);

        Assert.True(bytes.Length < new AgentTeamForge.Business.SpikeLimits().MaxFrameBytes / 4);
    }
}
