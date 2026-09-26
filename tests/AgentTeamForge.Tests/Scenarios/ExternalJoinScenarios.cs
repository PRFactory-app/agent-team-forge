using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;
using ModelContextProtocol.Protocol;
using System.Text;
using System.Text.Json;

namespace AgentTeamForge.Tests.Scenarios;

[Trait("Category", "Scenario")]
public sealed class ExternalJoinScenarios
{
    [Fact]
    public async Task External_read_pages_large_messages_without_losing_unread_work()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var rig = new SpikeRig();
        await rig.InitAsync();
        await rig.StartDaemonAsync();
        var (_, lead) = await rig.StartBridgeAsync("large-external-lead");
        var (_, member) = await rig.StartBridgeAsync(externalOnly: true);
        var ticket = (await SpikeRig.CallAsync(lead, "create_join_ticket", new() { ["name"] = "reader" })).Ticket!;
        var token = (await SpikeRig.CallAsync(member, "join_team", new()
        {
            ["session_id"] = ticket.SessionId,
            ["token"] = ticket.Token
        })).Member!.MemberToken;
        var escaped = new string('"', 65529) + "\"\\\n\té😀";
        var unicode = string.Concat(Enumerable.Repeat("😀", 32768));
        for (var i = 0; i < 50; i++)
        {
            Assert.True((await SpikeRig.CallAsync(lead, "send_message", new()
            {
                ["to"] = "reader",
                ["text"] = i % 2 == 0 ? escaped : unicode
            })).Ok);
        }

        var water = (await SpikeRig.CallAsync(member, "external_read", new()
        {
            ["member_token"] = token,
            ["limit"] = 0
        })).Inbox!;
        Assert.Empty(water.Messages);
        Assert.Equal(50, water.UnreadCount);
        Assert.True(water.HasMore);

        var seen = new List<long>();
        while (seen.Count < 50)
        {
            var call = await member.CallToolAsync("external_read", new Dictionary<string, object?>
            {
                ["member_token"] = token,
                ["full"] = true
            }, cancellationToken: TestContext.Current.CancellationToken);
            var json = Assert.IsType<TextContentBlock>(Assert.Single(call.Content)).Text;
            Assert.True(Encoding.UTF8.GetByteCount(json) < 2 * 1024 * 1024);
            var page = JsonSerializer.Deserialize(json, IpcJson.Default.IpcResponse)!.Inbox!;
            Assert.NotEmpty(page.Messages);
            seen.AddRange(page.Messages.Select(message => message.Seq));
            Assert.Equal(seen.Count < 50, page.HasMore);
        }
        Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i), seen);
        Assert.Empty((await SpikeRig.CallAsync(member, "external_read", new() { ["member_token"] = token })).Inbox!.Messages);
    }

    [Fact]
    public async Task Separate_member_bridge_joins_and_exchanges_messages_with_lead()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var fake = new TempStateDir();
        var fakeBin = fake.File("bin");
        Directory.CreateDirectory(fakeBin);
        var codex = Path.Combine(fakeBin, "codex");
        File.WriteAllText(codex, "#!/bin/sh\nprintf '%s\\n' \"$@\" >> \"$ATF_TEST_WAKE_LOG\"\n");
        File.SetUnixFileMode(codex, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var codexHome = fake.File("codex-home");
        Directory.CreateDirectory(Path.Combine(codexHome, "sessions"));
        var thread = Guid.NewGuid().ToString("D");
        File.WriteAllText(Path.Combine(codexHome, "sessions", "rollout-test-" + thread + ".jsonl"), "");
        var wakeLog = fake.File("wake.log");
        using var rig = new SpikeRig();
        await rig.InitAsync();
        await rig.StartDaemonWithEnvironmentAsync(new Dictionary<string, string>
        {
            ["PATH"] = fakeBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["ATF_TEST_WAKE_LOG"] = wakeLog
        });
        var (_, lead) = await rig.StartBridgeAsync("external-lead");
        var (_, member) = await rig.StartBridgeAsync(externalOnly: true);
        var tools = await member.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, tool => tool.Name == "join_team");
        Assert.DoesNotContain(tools, tool => tool.Name == "create_join_ticket");
        var leadTools = await lead.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(leadTools, tool => tool.Name == "send_message");
        var noRecipient = await SpikeRig.CallAsync(lead, "send_message", new() { ["text"] = "self" });
        Assert.Equal("member_not_found", noRecipient.Error);
        Assert.Contains("team-lead", noRecipient.ErrorDetail);
        Assert.Contains("none", noRecipient.ErrorDetail);

        var ticketCall = await lead.CallToolAsync("create_join_ticket", new Dictionary<string, object?> { ["name"] = "visitor" },
            cancellationToken: TestContext.Current.CancellationToken);
        var ticketText = Assert.IsType<TextContentBlock>(Assert.Single(ticketCall.Content)).Text;
        using (var ticketJson = JsonDocument.Parse(ticketText))
        {
            Assert.True(ticketJson.RootElement.GetProperty("success").GetBoolean());
            Assert.True(ticketJson.RootElement.TryGetProperty("join_prompt", out _));
        }
        var ticket = JsonSerializer.Deserialize(ticketText, IpcJson.Default.IpcResponse)!.Ticket!;
        var joinCall = await member.CallToolAsync("join_team", new Dictionary<string, object?>
        {
            ["session_id"] = ticket.SessionId,
            ["token"] = ticket.Token
        }, cancellationToken: TestContext.Current.CancellationToken);
        var joinText = Assert.IsType<TextContentBlock>(Assert.Single(joinCall.Content)).Text;
        using (var joinJson = JsonDocument.Parse(joinText))
        {
            Assert.StartsWith($"wam1:{ticket.SessionId}:", joinJson.RootElement.GetProperty("member_token").GetString());
        }
        var joined = JsonSerializer.Deserialize(joinText, IpcJson.Default.IpcResponse)!.Member!;
        Assert.Equal(joined, (await SpikeRig.CallAsync(member, "join_team", new()
        {
            ["session_id"] = ticket.SessionId,
            ["token"] = ticket.Token
        })).Member);
        Assert.True((await SpikeRig.CallAsync(member, "external_set_wake", new()
        {
            ["member_token"] = joined.MemberToken,
            ["codex_thread_id"] = thread,
            ["codex_home"] = codexHome
        })).Ok);
        var unknown = await SpikeRig.CallAsync(lead, "send_message", new() { ["to"] = "team-lead", ["text"] = "report" });
        Assert.Equal("member_not_found", unknown.Error);
        Assert.Contains(joined.Name, unknown.ErrorDetail);
        Assert.True((await SpikeRig.CallAsync(lead, "send_message", new() { ["to"] = joined.Name, ["text"] = "work" })).Ok);
        await Bounded.Until(() => File.Exists(wakeLog) && File.ReadAllText(wakeLog).Contains(thread, StringComparison.Ordinal), "Codex queue wake");
        Assert.Contains("queue", File.ReadAllText(wakeLog));
        Assert.Contains("external_read", File.ReadAllText(wakeLog));
        Assert.Equal("work", Assert.Single((await SpikeRig.CallAsync(member, "external_read", new()
        {
            ["member_token"] = joined.MemberToken
        })).Inbox!.Messages).Text);
        Assert.True((await SpikeRig.CallAsync(member, "external_send", new()
        {
            ["member_token"] = joined.MemberToken,
            ["text"] = "done"
        })).Ok);
        var readCall = await lead.CallToolAsync("read_messages", new Dictionary<string, object?>
        {
            ["from_agent"] = joined.Name,
            ["since_seq"] = 0,
            ["max_chars"] = 2
        }, cancellationToken: TestContext.Current.CancellationToken);
        using (var readJson = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(readCall.Content)).Text))
        {
            var root = readJson.RootElement;
            Assert.Equal(JsonValueKind.Null, root.GetProperty("cursors").ValueKind);
            Assert.Equal(1, root.GetProperty("seq").GetInt64());
            Assert.Equal(1, root.GetProperty("unread_count").GetInt32());
            Assert.False(root.GetProperty("has_more").GetBoolean());
            var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
            Assert.Equal("do", message.GetProperty("text").GetString());
            Assert.True(message.GetProperty("truncated").GetBoolean());
            Assert.Equal(4, message.GetProperty("full_len").GetInt32());
        }
        var left = await SpikeRig.CallAsync(member, "leave_team", new() { ["member_token"] = joined.MemberToken });
        Assert.False(left.AlreadyLeft);
        Assert.Equal(joined.Name, left.Name);
        var leftAgain = await SpikeRig.CallAsync(member, "leave_team", new() { ["member_token"] = joined.MemberToken });
        Assert.True(leftAgain.AlreadyLeft);
        Assert.Equal(joined.Name, leftAgain.Name);
        Assert.Equal("membership_revoked", (await SpikeRig.CallAsync(member, "external_read", new()
        {
            ["member_token"] = joined.MemberToken
        })).Error);
    }
}
