using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Scenarios;

[Trait("Category", "Scenario")]
public sealed class LeadSessionScenarios
{
    [Fact]
    public async Task Two_published_bridges_keep_own_jobs_and_restart_adopts_one_session()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        await rig.StartDaemonAsync();
        var (firstProcess, first) = await rig.StartBridgeAsync("lead-one");
        var (_, second) = await rig.StartBridgeAsync("lead-two");
        var firstSession = (await SpikeRig.CallAsync(first, "session_info", [])).Session!;
        var secondSession = (await SpikeRig.CallAsync(second, "session_info", [])).Session!;
        Assert.NotEqual(firstSession.SessionId, secondSession.SessionId);
        var firstJob = (await SpikeRig.CallAsync(first, "submit_job", new()
        {
            ["backend"] = "fake",
            ["instruction"] = "first",
            ["idempotency_key"] = "same"
        })).Job!.JobId;
        var secondJob = (await SpikeRig.CallAsync(second, "submit_job", new()
        {
            ["backend"] = "fake",
            ["instruction"] = "second",
            ["idempotency_key"] = "same"
        })).Job!.JobId;
        Assert.Equal([firstJob], (await SpikeRig.CallAsync(first, "list_jobs", [])).Page!.Jobs.Select(j => j.JobId));
        Assert.Equal([secondJob], (await SpikeRig.CallAsync(second, "list_jobs", [])).Page!.Jobs.Select(j => j.JobId));
        Assert.Equal(2, (await SpikeRig.CallAsync(second, "list_jobs", new() { ["all_workspace"] = true })).Page!.Jobs.Count);

        OwnedProcesses.KillAbruptly(firstProcess);
        var (_, restarted) = await rig.StartBridgeAsync("lead-one-restarted");
        var recovery = (await SpikeRig.CallAsync(restarted, "session_info", [])).Session!;
        Assert.Contains(recovery.RecoverableSessions, candidate => candidate.SessionId == firstSession.SessionId);
        var resumed = await SpikeRig.CallAsync(restarted, "resume_session", new() { ["session_id"] = firstSession.SessionId });
        Assert.True(resumed.Ok);
        Assert.Equal(firstSession.LeadToken, resumed.Session!.LeadToken);
        Assert.Equal([firstJob], (await SpikeRig.CallAsync(restarted, "list_jobs", [])).Page!.Jobs.Select(j => j.JobId));
        Assert.Equal([secondJob], (await SpikeRig.CallAsync(second, "list_jobs", [])).Page!.Jobs.Select(j => j.JobId));
    }
}
