using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Scenarios;

[Trait("Category", "Scenario")]
public sealed class BridgeReconnectScenarios
{
    [Fact]
    public async Task Live_bridge_restarts_dead_daemon_and_keeps_its_lead_identity()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var daemon = await rig.StartDaemonAsync();
        try
        {
            var (_, bridge) = await rig.StartBridgeAsync("reconnect-lead");
            var identity = (await SpikeRig.CallAsync(bridge, "session_info", [])).Session!;
            var first = await SpikeRig.CallAsync(bridge, "submit_job", new()
            {
                ["backend"] = "fake",
                ["instruction"] = "before",
                ["idempotency_key"] = "before"
            });
            Assert.True(first.Ok, first.Error);
            var firstId = first.Job!.JobId;

            OwnedProcesses.KillAbruptly(daemon);
            var listed = await SpikeRig.CallAsync(bridge, "list_jobs", []);
            Assert.Contains(listed.Page!.Jobs, job => job.JobId == firstId);
            Assert.True((await SpikeRig.CallAsync(bridge, "get_job", new() { ["job_id"] = firstId })).Ok);
            var restartedPid = DaemonLock.ReadOwnerPid(Path.Combine(rig.StateDir, "daemon.lock"));
            Assert.NotNull(restartedPid);
            Assert.NotEqual(daemon.Id, restartedPid);

            // Make submit itself the first request after a second daemon loss.
            Assert.Equal(0, (await rig.RunToExitAsync(["stop", "--state-dir", rig.StateDir])).Exit);
            var second = await SpikeRig.CallAsync(bridge, "submit_job", new()
            {
                ["backend"] = "fake",
                ["instruction"] = "after",
                ["idempotency_key"] = "after"
            });
            Assert.True(second.Ok, second.Error);
            Assert.NotEqual(restartedPid, DaemonLock.ReadOwnerPid(Path.Combine(rig.StateDir, "daemon.lock")));
            var rebound = (await SpikeRig.CallAsync(bridge, "session_info", [])).Session!;
            Assert.Equal(identity.SessionId, rebound.SessionId);
            Assert.Equal(identity.LeadToken, rebound.LeadToken);
            Assert.Contains((await SpikeRig.CallAsync(bridge, "list_jobs", [])).Page!.Jobs, job => job.JobId == second.Job!.JobId);
        }
        finally
        {
            Assert.Equal(0, (await rig.RunToExitAsync(["stop", "--state-dir", rig.StateDir])).Exit);
        }
    }

    [Fact]
    public async Task External_only_bridge_restarts_daemon_without_becoming_a_lead()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var daemon = await rig.StartDaemonAsync();
        try
        {
            var (_, lead) = await rig.StartBridgeAsync("external-reconnect-lead");
            var (_, member) = await rig.StartBridgeAsync(externalOnly: true);
            var session = (await SpikeRig.CallAsync(lead, "session_info", [])).Session!;
            var ticket = (await SpikeRig.CallAsync(lead, "create_join_ticket", new() { ["name"] = "visitor" })).Ticket!;
            var joined = (await SpikeRig.CallAsync(member, "join_team", new()
            {
                ["session_id"] = ticket.SessionId,
                ["token"] = ticket.Token
            })).Member!;

            OwnedProcesses.KillAbruptly(daemon);
            var sent = await SpikeRig.CallAsync(member, "external_send", new()
            {
                ["member_token"] = joined.MemberToken,
                ["text"] = "after restart"
            });
            Assert.True(sent.Ok, sent.Error);
            Assert.Equal("after restart", Assert.Single((await SpikeRig.CallAsync(lead, "read_messages", [])).Inbox!.Messages).Text);
            Assert.Equal(session.SessionId, (await SpikeRig.CallAsync(lead, "session_info", [])).Session!.SessionId);
        }
        finally
        {
            Assert.Equal(0, (await rig.RunToExitAsync(["stop", "--state-dir", rig.StateDir])).Exit);
        }
    }

    [Fact]
    public async Task Concurrent_bridges_start_one_daemon()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        try
        {
            var bridges = await Task.WhenAll(rig.StartBridgeAsync("concurrent-one"), rig.StartBridgeAsync("concurrent-two"));
            var sessions = await Task.WhenAll(bridges.Select(bridge => SpikeRig.CallAsync(bridge.Client, "session_info", [])));
            Assert.NotEqual(sessions[0].Session!.SessionId, sessions[1].Session!.SessionId);
            var log = File.ReadAllLines(Path.Combine(rig.StateDir, "daemon.log"));
            Assert.Single(log, line => line.StartsWith("[atf-daemon] ready pid=", StringComparison.Ordinal));
        }
        finally
        {
            await rig.RunToExitAsync(["stop", "--state-dir", rig.StateDir]);
        }
    }

    [Fact]
    public async Task Outcome_unknown_is_not_replayed_by_bridge()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        await rig.StartDaemonAsync("--test-fail-at", DurabilityCheckpoints.AcceptAfterCommit);
        try
        {
            var (_, bridge) = await rig.StartBridgeAsync("unknown-lead");
            var args = new Dictionary<string, object?>
            {
                ["backend"] = "fake",
                ["instruction"] = "once",
                ["idempotency_key"] = "unknown"
            };
            var lost = await SpikeRig.CallAsync(bridge, "submit_job", args);
            Assert.Equal(IpcProtocol.OutcomeUnknown, lost.Error);

            var recovered = await SpikeRig.CallAsync(bridge, "submit_job", args);
            Assert.Equal("existing", recovered.Outcome);
            await rig.WaitForStatusAsync(recovered.Job!.JobId, "completed");
            Assert.Equal(1, rig.Invocations(recovered.Job!.JobId));
        }
        finally
        {
            // The bridge restarts the daemon outside the rig's owned processes.
            await rig.RunToExitAsync(["stop", "--state-dir", rig.StateDir]);
        }
    }
}
