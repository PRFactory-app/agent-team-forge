using System.Text.Json.Nodes;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Tests.Support;
using ModelContextProtocol.Client;

namespace AgentTeamForge.Tests.Scenarios;

public sealed class LiveManagedChildRouting
{
    [Fact]
    public async Task Live_linux_claude_reports_to_parent_in_isolated_home()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ATF_LIVE_CHILD_CLAUDE") == "1", "set ATF_LIVE_CHILD_CLAUDE=1 with isolated authentication to run");
        Assert.True(OperatingSystem.IsLinux());
        using var home = new TempStateDir();
        using var rig = new SpikeRig();
        var env = new Dictionary<string, string>
        {
            ["HOME"] = home.Path,
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(home.Path, ".claude"),
            ["CODEX_HOME"] = Path.Combine(home.Path, ".codex"),
            ["PI_CODING_AGENT_DIR"] = Path.Combine(home.Path, ".pi"),
            ["XDG_CONFIG_HOME"] = Path.Combine(home.Path, ".config"),
            ["XDG_STATE_HOME"] = Path.Combine(home.Path, ".local", "state"),
            ["XDG_CACHE_HOME"] = Path.Combine(home.Path, ".cache"),
            ["PATH"] = Environment.GetEnvironmentVariable("ATF_LIVE_CLAUDE_BIN") + ":" + Environment.GetEnvironmentVariable("PATH"),
        };
        Assert.Equal(0, (await rig.RunToExitAsync(["init", "--state-dir", rig.StateDir])).Exit);
        using (var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            socket.Start();
            var port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
            var settings = Path.Combine(rig.StateDir, "launch-mode.json");
            File.WriteAllText(settings, $$"""{"mode":"headless","web_port":{{port}}}""");
            File.SetUnixFileMode(settings, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        await rig.StartDaemonWithEnvironmentAsync(env);
        var (_, parent) = await rig.StartBridgeAsync("live-parent", environment: env);
        var report = "ATF-LIVE-CHILD-" + Guid.NewGuid().ToString("N");
        var submitted = await SpikeRig.CallAsync(parent, "submit_job", new()
        {
            ["backend"] = "claude",
            ["model"] = "haiku",
            ["cwd"] = home.Path,
            ["instruction"] = $"Call mcp__agentteamforge__send_message with to=team-lead and text={report}. Then finish. No other work or tools are needed.",
            ["idempotency_key"] = report,
            ["timeout_s"] = 120,
        });
        Assert.True(submitted.Ok, submitted.Error);
        var job = await Bounded.Until(async () =>
        {
            var response = await rig.GetAsync(submitted.Job!.JobId);
            return response.Job!.Status is "queued" or "running" ? null : response;
        }, "live Claude completion", TimeSpan.FromSeconds(150));
        Assert.True(job.Job!.Status == "completed", $"status={job.Job.Status}, reason={job.Job.ReasonCode}, output={job.Job.Result}");
        Assert.Equal(report, Assert.Single((await SpikeRig.CallAsync(parent, "read_messages", [])).Inbox!.Messages).Text);
    }
}

[Trait("Category", "Scenario")]
public sealed class ManagedChildRoutingScenarios
{
    [Fact]
    public async Task Independent_nested_leads_route_upstream_and_resume_without_cross_routing()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        await rig.StartDaemonAsync();
        var (_, parentA) = await rig.StartBridgeAsync("parent-a");
        var (_, parentB) = await rig.StartBridgeAsync("parent-b");

        async Task<(McpClient Client, string Job)> Child(McpClient parent, string key)
        {
            var submitted = await SpikeRig.CallAsync(parent, "submit_job", new()
            {
                ["backend"] = "fake",
                ["instruction"] = key,
                ["idempotency_key"] = key,
            });
            Assert.True(submitted.Ok);
            var job = submitted.Job!.JobId;
            await rig.WaitForStatusAsync(job, "completed");
            var config = JsonNode.Parse(File.ReadAllText(ManagedChildContext.ConfigPath(rig.StateDir, job)))!;
            var args = config["mcpServers"]![ManagedChildContext.ServerName]!["args"]!.AsArray();
            Assert.Equal(rig.StateDir, args[2]!.GetValue<string>());
            var (_, client) = await rig.StartBridgeAsync(managedContext: args[4]!.GetValue<string>());
            return (client, job);
        }

        var (nestedA, jobA) = await Child(parentA, "nested-a");
        var (nestedB, _) = await Child(parentB, "nested-b");
        var (childA, _) = await Child(nestedA, "child-a");
        var (childB, _) = await Child(nestedB, "child-b");
        Assert.True((await SpikeRig.CallAsync(childA, "send_message", new() { ["text"] = "report-a" })).Ok);
        Assert.True((await SpikeRig.CallAsync(childB, "send_message", new() { ["to"] = "team-lead", ["text"] = "report-b" })).Ok);
        Assert.Equal("report-a", Assert.Single((await SpikeRig.CallAsync(nestedA, "read_messages", [])).Inbox!.Messages).Text);
        Assert.Equal("report-b", Assert.Single((await SpikeRig.CallAsync(nestedB, "read_messages", [])).Inbox!.Messages).Text);
        Assert.Empty((await SpikeRig.CallAsync(parentA, "read_messages", [])).Inbox!.Messages);
        Assert.Empty((await SpikeRig.CallAsync(parentB, "read_messages", [])).Inbox!.Messages);
        var unknown = await SpikeRig.CallAsync(childA, "send_message", new() { ["to"] = "typo", ["text"] = "lost?" });
        Assert.Equal("member_not_found", unknown.Error);
        Assert.Empty((await SpikeRig.CallAsync(nestedA, "read_messages", [])).Inbox!.Messages);

        var follow = await SpikeRig.CallAsync(parentA, "follow_up", new()
        {
            ["job_id"] = jobA,
            ["instruction"] = "again",
            ["idempotency_key"] = "again",
        });
        Assert.True(follow.Ok);
        await rig.WaitForStatusAsync(follow.Job!.JobId, "completed");
        var originalConfig = File.ReadAllText(ManagedChildContext.ConfigPath(rig.StateDir, jobA));
        Assert.Equal(originalConfig, File.ReadAllText(ManagedChildContext.ConfigPath(rig.StateDir, follow.Job.JobId)));
        var contextPath = JsonNode.Parse(originalConfig)!["mcpServers"]![ManagedChildContext.ServerName]!["args"]![4]!.GetValue<string>();
        var (_, resumed) = await rig.StartBridgeAsync("different-process-parent", managedContext: contextPath);
        Assert.Equal((await SpikeRig.CallAsync(nestedA, "session_info", [])).Session!.SessionId,
            (await SpikeRig.CallAsync(resumed, "session_info", [])).Session!.SessionId);
        Assert.True((await SpikeRig.CallAsync(resumed, "send_message", new() { ["text"] = "resumed-report" })).Ok);
        Assert.Equal("resumed-report", Assert.Single((await SpikeRig.CallAsync(parentA, "read_messages", [])).Inbox!.Messages).Text);
        Assert.Empty((await SpikeRig.CallAsync(parentB, "read_messages", [])).Inbox!.Messages);
    }
}
