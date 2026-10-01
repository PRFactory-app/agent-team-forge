using System.Diagnostics;
using System.Text.Json;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;
using ModelContextProtocol;

namespace AgentTeamForge.Tests.Scenarios;

/// <summary>Argument errors through the real bridge and daemon, and daemon autostart for the prune command.</summary>
[Trait("Category", "Scenario")]
public sealed class BridgeArgumentScenarios
{
    [Fact]
    public async Task Wrong_typed_or_missing_arguments_are_rejected_naming_the_field()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var (_, bridge) = await rig.StartBridgeAsync();

        var numeric = await SpikeRig.CallAsync(bridge, "get_job", new() { ["job_id"] = 123 });
        Assert.Equal((JobErrors.InvalidRequest, "job_id must be a string."), (numeric.Error, numeric.ErrorDetail));
        var missing = await SpikeRig.CallAsync(bridge, "get_job", []);
        Assert.Equal((JobErrors.InvalidRequest, "Missing required argument job_id."), (missing.Error, missing.ErrorDetail));
        var range = await SpikeRig.CallAsync(bridge, "submit_job",
            new() { ["backend"] = "fake", ["instruction"] = "x", ["idempotency_key"] = "k", ["timeout_s"] = 0 });
        Assert.Equal("timeout_s must be an integer from 1 to 86400.", range.ErrorDetail);
        var keyless = await SpikeRig.CallAsync(bridge, "send_message", new() { ["job_id"] = "job_x", ["text"] = "next" });
        Assert.Equal("Missing argument idempotency_key: it is required with job_id.", keyless.ErrorDetail);

        Assert.True((await SpikeRig.CallAsync(bridge, "list_jobs", [])).Ok); // the bridge keeps serving
    }

    [Fact]
    public async Task Schema_valid_arguments_the_daemon_refuses_are_rejected_naming_the_field()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var (_, bridge) = await rig.StartBridgeAsync();

        var empty = await SpikeRig.CallAsync(bridge, "submit_job",
            new() { ["backend"] = "fake", ["instruction"] = "", ["idempotency_key"] = "k" });
        Assert.Equal((JobErrors.InvalidRequest, "Invalid instruction: must not be empty."), (empty.Error, empty.ErrorDetail));
        var longKey = await SpikeRig.CallAsync(bridge, "submit_job",
            new() { ["backend"] = "fake", ["instruction"] = "x", ["idempotency_key"] = new string('k', 129) });
        Assert.Equal("Invalid idempotency_key: must be 1 to 128 characters and not blank.", longKey.ErrorDetail);
        Assert.StartsWith("Invalid since: ", (await SpikeRig.CallAsync(bridge, "list_jobs", new() { ["since"] = "yesterday" })).ErrorDetail);
        Assert.StartsWith("Invalid cursor: ", (await SpikeRig.CallAsync(bridge, "list_jobs", new() { ["cursor"] = "page-2" })).ErrorDetail);
        var name = await SpikeRig.CallAsync(bridge, "set_session_name", new() { ["name"] = new string('n', 65) });
        Assert.Equal((JobErrors.InvalidRequest, "Invalid name: at most 64 characters without control characters."), (name.Error, name.ErrorDetail));
    }

    [Fact]
    public async Task Raw_json_rpc_errors_are_answered_and_the_bridge_keeps_serving()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        var info = new ProcessStartInfo(SpikeRig.Binary)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "mcp", "--state-dir", rig.StateDir }) { info.ArgumentList.Add(arg); }
        using var bridge = Process.Start(info)!;
        bridge.ErrorDataReceived += (_, _) => { };
        bridge.BeginErrorReadLine();
        try
        {
            await SendAsync(bridge, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""");
            await ReadAsync(bridge);
            await SendAsync(bridge, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            // No McpClient can send these: one is not JSON, the other carries an escape no encoder can write.
            await SendAsync(bridge, "{not json}");
            using (var parse = await ReadAsync(bridge))
            {
                Assert.Equal(JsonValueKind.Null, parse.RootElement.GetProperty("id").ValueKind);
                Assert.Equal((int)McpErrorCode.ParseError, parse.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            }
            await SendAsync(bridge, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"submit_job","arguments":{"backend":"fake","instruction":"x\ud800y","idempotency_key":"w1"}}}""");
            using (var surrogate = await ReadAsync(bridge))
            {
                Assert.Equal(2, surrogate.RootElement.GetProperty("id").GetInt32());
                var error = surrogate.RootElement.GetProperty("error");
                Assert.Equal((int)McpErrorCode.InvalidParams, error.GetProperty("code").GetInt32());
                Assert.Contains("instruction", error.GetProperty("message").GetString(), StringComparison.Ordinal);
            }
            await SendAsync(bridge, """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""");
            using var tools = await ReadAsync(bridge);
            Assert.Equal(3, tools.RootElement.GetProperty("id").GetInt32());
            Assert.True(tools.RootElement.TryGetProperty("result", out _));
        }
        finally
        {
            // Never Process.Kill(entireProcessTree: true) on a direct child: on macOS it stops the child first and
            // this process's SIGCHLD handler then spins on it forever (see OwnedProcessTermination).
            OwnedProcessTermination.Kill(bridge);
            await bridge.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
    }

    static async Task SendAsync(Process bridge, string line)
    {
        await bridge.StandardInput.WriteAsync(line + "\n");
        await bridge.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
    }

    static async Task<JsonDocument> ReadAsync(Process bridge)
    {
        using var deadline = new CancellationTokenSource(Bounded.ScenarioDeadline);
        var line = await bridge.StandardOutput.ReadLineAsync(deadline.Token);
        Assert.NotNull(line);
        return JsonDocument.Parse(line);
    }

    [Fact]
    public async Task Prune_starts_the_daemon_like_other_client_commands()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();

        var (exit, stdout, stderr) = await rig.RunToExitAsync(["prune", "--dry-run", "--state-dir", rig.StateDir]);

        Assert.True(exit == 0, stderr);
        Assert.True(JsonSerializer.Deserialize(stdout, IpcJson.Default.IpcResponse)!.Ok, stdout);
        var (worktreesExit, worktreesOut, worktreesErr) = await rig.RunToExitAsync(["worktrees", "prune", "--dry-run", "--state-dir", rig.StateDir]);
        Assert.True(worktreesExit == 0, worktreesErr);
        Assert.Contains("worktrees:", worktreesOut, StringComparison.Ordinal);
    }
}
