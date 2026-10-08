using System.Text.Json;
using AgentTeamForge.Business.Features.Usage;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Host.Features.PRFactory;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class UsageReportTests
{
    static JobRecord Job(string id, string backend, string? session, string options = "") =>
        new(id, "p", "team", "lead", id, "work", options, "succeeded", null, null, 1, backend, null, null, session);

    static readonly PRFactoryWorkItem Item = new() { Id = Guid.NewGuid(), AgentType = PRFactoryAgentType.ClaudeCode, Prompt = "work" };

    [Fact]
    public void Single_backend_team_sums_distinct_sessions()
    {
        var reads = new List<string>();
        var lead = Job("a", "claude", "s1");
        var report = PRFactoryWorkItems.UsageFor(Item, lead, [lead, Job("b", "claude", "s1"), Job("c", "claude", "s2")],
            j => { reads.Add(j.SessionId!); return new TokenUsage(10, 2, 3, 4); })!;
        Assert.Equal(["s1", "s2"], reads);
        Assert.Equal(PRFactoryAgentType.ClaudeCode, report.Backend);
        Assert.Equal((20L, 4L, 6L, 8L), (report.InputTokens!.Value, report.OutputTokens!.Value, report.CacheReadTokens!.Value, report.CacheWriteTokens!.Value));
    }

    [Fact]
    public void Unreadable_session_omits_figures_but_keeps_backend()
    {
        var lead = Job("a", "codex", "s1", "model=gpt-5");
        var report = PRFactoryWorkItems.UsageFor(Item, lead, [lead, Job("b", "codex", null)], _ => new TokenUsage(1, 1, 1, null))!;
        Assert.Equal(new PRFactoryUsageReport(PRFactoryAgentType.Codex, "gpt-5", null, null, null, null), report);
        Assert.Equal(new PRFactoryUsageReport(PRFactoryAgentType.Codex, "gpt-5", null, null, null, null),
            PRFactoryWorkItems.UsageFor(Item, lead, [lead], _ => null));
    }

    [Fact]
    public void Mixed_backend_team_reports_lead_backend_and_drops_cache_write()
    {
        var lead = Job("a", "claude", "s1");
        var report = PRFactoryWorkItems.UsageFor(Item, lead, [lead, Job("b", "codex", "s1")],
            j => j.Backend == "claude" ? new TokenUsage(10, 2, 3, 4) : new TokenUsage(5, 1, 7, null))!;
        Assert.Equal(new PRFactoryUsageReport(PRFactoryAgentType.ClaudeCode, null, 15, 3, 10, null), report);
    }

    [Fact]
    public void Joined_external_member_makes_figures_unknown_but_keeps_lead_metadata()
    {
        var lead = Job("a", "claude", "s1", "model=opus");
        var report = PRFactoryWorkItems.UsageFor(Item, lead, [lead], _ => new TokenUsage(10, 2, 3, 4), hasExternalMembers: true);
        Assert.Equal(new PRFactoryUsageReport(PRFactoryAgentType.ClaudeCode, "opus", null, null, null, null), report);
    }

    [Fact]
    public void Worker_native_item_sends_no_usage() =>
        Assert.Null(PRFactoryWorkItems.UsageFor(Item, null, [], _ => throw new InvalidOperationException()));

    [Fact]
    public void Model_prefers_job_option_then_item()
    {
        var item = new PRFactoryWorkItem { Id = Guid.NewGuid(), Model = "sonnet", Prompt = "work" };
        Assert.Equal("opus", PRFactoryWorkItems.UsageFor(item, Job("a", "claude", "s", "effort=high;model=opus"), [], _ => null)!.Model);
        Assert.Equal("sonnet", PRFactoryWorkItems.UsageFor(item, Job("a", "claude", "s"), [], _ => null)!.Model);
        Assert.Null(PRFactoryWorkItems.UsageFor(Item, Job("a", "claude", "s"), [], _ => null)!.Model);
    }

    [Fact]
    public void Serialised_report_has_exactly_the_contract_keys_and_omits_nulls()
    {
        var request = new PRFactoryCompletionRequest(true, "done", null, null, string.Empty, Guid.NewGuid(),
            Usage: new PRFactoryUsageReport(PRFactoryAgentType.Codex, "gpt-5", 120, 30, 80, null));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(request, PRFactoryWorkItemJson.Default.PRFactoryCompletionRequest));
        var usage = doc.RootElement.GetProperty("usage");
        Assert.Equal(["backend", "model", "inputTokens", "outputTokens", "cacheReadTokens"], usage.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Codex", usage.GetProperty("backend").GetString());
        Assert.Equal(120, usage.GetProperty("inputTokens").GetInt64());
        Assert.Equal("120", usage.GetProperty("inputTokens").GetRawText());

        using var none = JsonDocument.Parse(JsonSerializer.Serialize(request with { Usage = null }, PRFactoryWorkItemJson.Default.PRFactoryCompletionRequest));
        Assert.False(none.RootElement.TryGetProperty("usage", out _));
    }
}
