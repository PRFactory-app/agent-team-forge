using AgentTeamForge.Tests.Support;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.Tests.Scenarios;

[Trait("Category", "Scenario")]
public sealed class LeadSessionScenarios
{
    [Fact]
    public async Task Lead_can_stop_a_fenced_job_visible_in_its_workspace()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        await rig.StartDaemonAsync();
        var (_, first) = await rig.StartBridgeAsync("lead-one", environment: new Dictionary<string, string> { ["CLAUDE_CODE_SESSION_ID"] = "old-native" });
        var (_, second) = await rig.StartBridgeAsync("lead-two", environment: new Dictionary<string, string> { ["CLAUDE_CODE_SESSION_ID"] = "new-native" });
        var submitted = await SpikeRig.CallAsync(first, "submit_job", new()
        {
            ["backend"] = "fake",
            ["instruction"] = "exit",
            ["idempotency_key"] = "fenced",
            ["behavior"] = AgentTeamForge.Business.Features.Jobs.FakeBehavior.ExitAfterReceipt
        });
        var jobId = submitted.Job!.JobId;
        await rig.WaitForStatusAsync(jobId, AgentTeamForge.DAL.Features.Jobs.JobStatus.NeedsReconciliation);
        Assert.True((await SpikeRig.CallAsync(second, "get_job", new() { ["job_id"] = jobId })).Ok);
        var stopped = await SpikeRig.CallAsync(second, "stop_job", new() { ["job_id"] = jobId });
        Assert.True(stopped.Ok);
        Assert.Equal("cancelled", stopped.Job!.Status);
    }

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

    [Fact]
    public async Task Resume_refuses_a_live_claude_owner_until_its_process_is_gone()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        await rig.StartDaemonAsync();
        var (ownerProcess, owner) = await rig.StartBridgeAsync("lead-one", environment: new Dictionary<string, string> { ["CLAUDE_CODE_SESSION_ID"] = "native-a" });
        var ownerSession = (await SpikeRig.CallAsync(owner, "session_info", [])).Session!;
        var jobId = (await SpikeRig.CallAsync(owner, "submit_job", new() { ["backend"] = "fake", ["instruction"] = "x", ["idempotency_key"] = "k" })).Job!.JobId;
        // Bind the owner's session to a Claude wake target whose host pid is the live owner bridge.
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(rig.StateDir, "jobs.db")}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO wake_targets(target_key,generation,kind,address,secret,home,registered_at)
                VALUES ('claude:owner',1,'claude','/nonexistent.sock','token',$pid,$now);
                UPDATE lead_sessions SET wake_key='claude:owner' WHERE session_id=$id;
                """;
            command.Parameters.AddWithValue("$pid", ownerProcess.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", ownerSession.SessionId);
            command.ExecuteNonQuery();
        }
        var (_, other) = await rig.StartBridgeAsync("lead-two", environment: new Dictionary<string, string> { ["CLAUDE_CODE_SESSION_ID"] = "native-b" });
        Assert.DoesNotContain((await SpikeRig.CallAsync(other, "session_info", [])).Session!.RecoverableSessions,
            s => s.SessionId == ownerSession.SessionId);
        var denied = await SpikeRig.CallAsync(other, "get_job", new() { ["job_id"] = jobId });
        Assert.Equal("owned_by_live_lead", denied.Error);
        Assert.Equal(ownerSession.SessionId, denied.Recovery!.Arguments.SessionId);
        var refused = await SpikeRig.CallAsync(other, "resume_session", new() { ["session_id"] = ownerSession.SessionId });
        Assert.Equal("session_owned", refused.Error);

        OwnedProcesses.KillAbruptly(ownerProcess);
        var listed = Assert.Single((await SpikeRig.CallAsync(other, "session_info", [])).Session!.RecoverableSessions,
            s => s.SessionId == ownerSession.SessionId);
        Assert.Equal(("native-a", false), (listed.OwnerNativeId, listed.OwnerLive));
        Assert.True((await SpikeRig.CallAsync(other, "get_job", new() { ["job_id"] = jobId })).Ok);
        Assert.Equal(ownerSession.SessionId, (await SpikeRig.CallAsync(other, "session_info", [])).Session!.SessionId);
    }
}
