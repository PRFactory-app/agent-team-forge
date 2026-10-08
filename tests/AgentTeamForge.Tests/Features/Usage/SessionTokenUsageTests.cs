using System.Text.Json;
using AgentTeamForge.Business.Features.Usage;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Sessions;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Usage;

public sealed class SessionTokenUsageTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "atf-usage-" + Guid.NewGuid().ToString("N"));
    readonly SessionTokenUsage reader = new();

    string Transcript(string kind, string id, string content)
    {
        var dir = Path.Combine(home, kind == "claude" ? "projects" : "sessions", "test");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, kind == "codex" ? "rollout-test-" + id + ".jsonl" : id + ".jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    static string Claude(string id, long input = 10) => $$$$$"""
        {"type":"assistant","sessionId":"claude-session","isSidechain":false,"message":{"id":"{{{{{id}}}}}","usage":{"input_tokens":{{{{{input}}}}},"output_tokens":2,"cache_read_input_tokens":3,"cache_creation_input_tokens":4}}}
        """ + "\n";
    [Fact]
    public void Large_first_Claude_chain_record_preserves_usage_and_native_receipt()
    {
        var user = $$$"""{"type":"user","isSidechain":false,"sessionId":"claude-session","message":{"role":"user","content":"{{{new string('x', 270_553)}}} atf-corr:large"}}""";
        Transcript("claude", "claude-session", user + "\n" + Claude("one")
            + "{\"type\":\"assistant\",\"sessionId\":\"claude-session\",\"message\":{\"role\":\"assistant\",\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"DONE\"}]}}\n");
        Assert.Equal(19, reader.Read("claude", "claude-session", home)!.Total);
        var receipt = AgentTeamForge.Business.Features.Agents.Terminals.InteractiveTranscriptReader.ReadClaudeSession(home, "claude-session", "large");
        Assert.NotNull(receipt);
        Assert.True(receipt.Completed);
        Assert.Equal("DONE", receipt.Message);
    }

    [Theory]
    [InlineData("cli")]
    [InlineData("exec")]
    [InlineData("vscode")]
    public void Large_Codex_header_accepts_parent_sources_for_usage_and_preserves_receipt_rules(string source)
    {
        var header = $$$"""{"type":"session_meta","payload":{"id":"thread","source":"{{{source}}}","instructions":"{{{new string('x', 100_000)}}}"}}""";
        Transcript("codex", "thread", header + "\n" + Codex(100, 40, 10)
            + "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n"
            + "{\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"atf-corr:large\"}}\n"
            + "{\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"DONE\"}}\n"
            + "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}\n");
        Assert.Equal(new TokenUsage(60, 10, 40, null), reader.Read("codex", "thread", home));
        var receipt = AgentTeamForge.Business.Features.Agents.Terminals.InteractiveTranscriptReader.ReadCodexThread(home, "thread", "large");
        if (source == "cli") { Assert.True(receipt!.Completed); }
        else { Assert.Null(receipt); }
    }

    [Fact]
    public void Codex_subagent_transcript_is_not_counted()
    {
        Transcript("codex", "thread", "{\"type\":\"session_meta\",\"payload\":{\"id\":\"thread\",\"source\":{\"subagent\":\"review\"}}}\n" + Codex(100, 40, 10));
        Assert.Null(reader.Read("codex", "thread", home));
    }

    [Fact]
    public void Deleted_transcript_clears_cached_usage_and_can_be_relocated()
    {
        var path = Transcript("claude", "claude-session", Claude("one"));
        Assert.Equal(19, reader.Read("claude", "claude-session", home)!.Total);
        File.Delete(path);
        Assert.Null(reader.Read("claude", "claude-session", home));
        var moved = Path.Combine(home, "projects", "moved");
        Directory.CreateDirectory(moved);
        File.WriteAllText(Path.Combine(moved, "claude-session.jsonl"), Claude("one", 7));
        Assert.Equal(new TokenUsage(7, 2, 3, 4), reader.Read("claude", "claude-session", home));
    }

    [Theory]
    [InlineData("codex", "codex")]
    [InlineData("claude", "claude")]
    [InlineData(null, "claude")]
    public void Native_binding_prefers_actual_host_when_both_environment_ids_exist(string? host, string expected)
    {
        Assert.Equal(expected, JobsMcpBridge.NativeKind(host, "claude-session", "codex-thread"));
    }
    [Fact]
    public void Claude_dedupes_appends_and_waits_for_complete_lines()
    {
        var line = Claude("one");
        var path = Transcript("claude", "claude-session", line + line + line + Claude("two"));
        Assert.Equal(new TokenUsage(20, 4, 6, 8), reader.Read("claude", "claude-session", home));
        Assert.Equal(38, reader.Read("claude", "claude-session", home)!.Total);
        var next = Claude("three");
        File.AppendAllText(path, next[..^1]);
        Assert.Equal(20, reader.Read("claude", "claude-session", home)!.Input);
        File.AppendAllText(path, "\n");
        Assert.Equal(30, reader.Read("claude", "claude-session", home)!.Input);
        File.AppendAllText(path, "invalid\n" + Claude("four"));
        Assert.Equal(40, reader.Read("claude", "claude-session", home)!.Input);
    }

    [Fact]
    public void Shrinking_transcript_resets_totals_and_dedupe()
    {
        var path = Transcript("claude", "claude-session", Claude("one") + Claude("two"));
        Assert.Equal(20, reader.Read("claude", "claude-session", home)!.Input);
        File.WriteAllText(path, Claude("one", 7));
        Assert.Equal(new TokenUsage(7, 2, 3, 4), reader.Read("claude", "claude-session", home));
        File.Delete(path);
        Assert.Null(reader.Read("claude", "claude-session", home));
    }

    static string Codex(long input, long cached, long output) => $$$$$"""
        {"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":{{{{{input}}}}},"cached_input_tokens":{{{{{cached}}}}},"output_tokens":{{{{{output}}}}}}}}}
        """ + "\n";
    [Fact]
    public void Codex_uses_last_cumulative_value_and_excludes_cached_input()
    {
        var path = Transcript("codex", "thread", "{\"type\":\"session_meta\",\"payload\":{\"id\":\"thread\",\"source\":\"cli\"}}\n"
            + Codex(100, 40, 10) + Codex(200, 80, 30));
        Assert.Equal(new TokenUsage(120, 30, 80, null), reader.Read("codex", "thread", home));
        File.AppendAllText(path, Codex(300, 90, 40));
        Assert.Equal(340, reader.Read("codex", "thread", home)!.Total);
    }

    [Fact]
    public void Codex_reports_no_cache_write_figure()
    {
        Transcript("codex", "thread", "{\"type\":\"session_meta\",\"payload\":{\"id\":\"thread\",\"source\":\"cli\"}}\n"
            + Codex(100, 40, 10) + Codex(200, 80, 30));
        var usage = SessionTokenUsage.ReadFinal("codex", "thread", home)!;
        Assert.Equal(120, usage.Input);
        Assert.Equal(30, usage.Output);
        Assert.Equal(80, usage.CacheRead);
        Assert.Null(usage.CacheWrite);
    }

    [Fact]
    public void Claude_reports_all_four_figures()
    {
        Transcript("claude", "claude-session", Claude("one") + Claude("two") + Claude("one"));
        Assert.Equal(new TokenUsage(20, 4, 6, 8), SessionTokenUsage.ReadFinal("claude", "claude-session", home));
    }

    [Fact]
    public void Missing_figure_is_unknown_not_zero()
    {
        Transcript("claude", "claude-session", Claude("one")
            + "{\"type\":\"assistant\",\"message\":{\"id\":\"two\",\"usage\":{\"input_tokens\":5,\"output_tokens\":1,\"cache_read_input_tokens\":2}}}\n");
        Assert.Equal(new TokenUsage(15, 3, 5, null), SessionTokenUsage.ReadFinal("claude", "claude-session", home));
        Assert.Equal(new TokenUsage(15, 3, 5, null), reader.Read("claude", "claude-session", home));
    }

    [Fact]
    public void ReadFinal_reads_a_transcript_larger_than_one_chunk()
    {
        var padding = new string('x', 4_800);
        var lines = new System.Text.StringBuilder();
        for (var i = 0; i < 2_000; i++)
        {
            lines.Append("{\"type\":\"assistant\",\"sessionId\":\"claude-session\",\"isSidechain\":false,\"pad\":\"").Append(padding).Append("\",\"message\":{\"id\":\"m").Append(i)
                .Append("\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}");
            if (i < 1_999) { lines.Append('\n'); }
        }
        var path = Transcript("claude", "claude-session", lines.ToString());
        Assert.True(new FileInfo(path).Length > 9 * 1024 * 1024);
        Assert.True(reader.Read("claude", "claude-session", home)!.Input < 2_000);
        Assert.Equal(2_000, SessionTokenUsage.ReadFinal("claude", "claude-session", home)!.Input);
    }

    [Fact]
    public void ReadFinal_reports_unknown_when_an_oversized_usage_record_cannot_be_parsed()
    {
        var huge = "{\"type\":\"assistant\",\"sessionId\":\"claude-session\",\"message\":{\"id\":\"big\",\"content\":\""
            + new string('x', 9 * 1024 * 1024)
            + "\",\"usage\":{\"input_tokens\":500,\"output_tokens\":5,\"cache_read_input_tokens\":5,\"cache_creation_input_tokens\":5}}}\n";
        Transcript("claude", "claude-session", Claude("one") + huge + Claude("three"));
        Assert.Null(SessionTokenUsage.ReadFinal("claude", "claude-session", home));
    }

    [Fact]
    public void ReadFinal_keeps_Codex_cumulative_total_after_an_oversized_line()
    {
        var huge = "{\"type\":\"response_item\",\"payload\":{\"text\":\"" + new string('x', 9 * 1024 * 1024) + "\"}}\n";
        Transcript("codex", "thread", "{\"type\":\"session_meta\",\"payload\":{\"id\":\"thread\",\"source\":\"cli\"}}\n"
            + Codex(100, 40, 10) + huge + Codex(200, 80, 30));
        Assert.Equal(new TokenUsage(120, 30, 80, null), SessionTokenUsage.ReadFinal("codex", "thread", home));
        Transcript("codex", "thread", "{\"type\":\"session_meta\",\"payload\":{\"id\":\"thread\",\"source\":\"cli\"}}\n"
            + Codex(100, 40, 10) + huge);
        Assert.Null(SessionTokenUsage.ReadFinal("codex", "thread", home));
    }

    [Fact]
    public void Pi_sums_assistant_usage_and_missing_sessions_are_unknown()
    {
        Transcript("pi", "pi-session", "{\"type\":\"session\",\"id\":\"pi-session\"}\n"
            + "{\"type\":\"message\",\"message\":{\"role\":\"assistant\",\"usage\":{\"input\":10,\"output\":2,\"cacheRead\":3,\"cacheWrite\":4}}}\n");
        Assert.Equal(new TokenUsage(10, 2, 3, 4), reader.Read("pi", "pi-session", home));
        Assert.Null(reader.Read("claude", "missing", home));
        Assert.Null(reader.Read("fake", "missing", home));
        Assert.Null(reader.Read("claude", "../invalid", home));
        Assert.Null(SessionTokenUsage.ReadFinal("claude", "missing", home));
        Assert.Null(SessionTokenUsage.ReadFinal("cursor", "pi-session", home));
    }

    [Fact]
    public void Lead_binding_is_stored_replaced_and_falls_back_to_codex_wake()
    {
        using var f = new JobFixture();
        var sessions = new LeadSessionStore(f.Database);
        var accept = f.Accept();
        var endpoint = new JobsEndpoint(accept, f.Get(), new FollowUpJob(f.Store, JobFixture.Operator, accept), f.List(),
            new StopJob(f.Store, JobFixture.Operator, _ => { }), new AgentTeamForge.DAL.Sqlite.DurabilityCheckpoints(null),
            () => { }, sessions: sessions, usage: reader);
        var lead = endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.SessionStart,
            Workspace = Environment.CurrentDirectory,
            BindingKey = "binding",
            NativeKind = "claude",
            NativeSessionId = "native-one",
            NativeHome = home
        }).Session!;
        Assert.Equal(new NativeSessionBinding(lead.SessionId, "claude", "native-one", home), Assert.Single(sessions.NativeBindings([lead.SessionId])));
        Assert.True(endpoint.Handle(new IpcRequest
        {
            Op = IpcProtocol.SessionResume,
            LeadSessionId = lead.SessionId,
            Workspace = Environment.CurrentDirectory,
            BindingKey = "new-binding",
            NativeKind = "codex",
            NativeSessionId = "native-two",
            NativeHome = home
        }).Ok);
        Assert.Equal("native-two", Assert.Single(sessions.NativeBindings([lead.SessionId])).NativeId);
        var old = sessions.Start("workspace", "old-binding");
        var target = new WakeStore(f.Database).Register("codex:usage", "codex", "wake-thread", "", home);
        sessions.BindWake(old.SessionId, target.Key, target.Generation);
        Assert.Equal(new NativeSessionBinding(old.SessionId, "codex", "wake-thread", home), Assert.Single(sessions.NativeBindings([old.SessionId])));
        sessions.Close(old.SessionId, "workspace");
        Assert.Empty(sessions.NativeBindings([old.SessionId]));
    }

    [Fact]
    public void Web_list_reads_only_leads_on_its_page_and_mcp_does_not_read_usage()
    {
        using var f = new JobFixture();
        Transcript("claude", "claude-session", Claude("one"));
        var sessions = new LeadSessionStore(f.Database);
        var lead = sessions.Start("workspace", "binding", "claude", "claude-session", home);
        var other = sessions.Start("workspace", "other", "claude", "missing", home);
        var accept = f.Accept();
        accept.Execute(new SubmitJobRequest("page", "hello", null, false) { LeadSessionId = lead.SessionId });
        var endpoint = new JobsEndpoint(accept, f.Get(), new FollowUpJob(f.Store, JobFixture.Operator, accept), f.List(),
            new StopJob(f.Store, JobFixture.Operator, _ => { }), new AgentTeamForge.DAL.Sqlite.DurabilityCheckpoints(null),
            () => { }, sessions: sessions, usage: reader);
        Assert.Null(endpoint.Handle(new IpcRequest { Op = IpcProtocol.JobList }).LeadTokens);
        var web = endpoint.Handle(new IpcRequest { Op = IpcProtocol.JobList, IncludeUsage = true });
        Assert.Equal(19, Assert.Single(web.LeadTokens!).Value!.Total);
        Assert.False(web.LeadTokens!.ContainsKey(other.SessionId));
        var child = sessions.Start("workspace", "managed-child:job-child", "claude", "claude-session", home);
        accept.Execute(new SubmitJobRequest("nested-page", "hello", null, false) { LeadSessionId = child.SessionId });
        var nested = endpoint.Handle(new IpcRequest { Op = IpcProtocol.JobList, IncludeUsage = true });
        Assert.Null(nested.LeadTokens![child.SessionId]);
        Assert.Empty(sessions.NativeBindings([child.SessionId]));
    }

    [Fact]
    public void Default_list_response_keeps_usage_out_of_mcp_json()
    {
        using var f = new JobFixture();
        f.Submit("usage-contract");
        var response = new IpcResponse(true, Page: f.List().Execute(new()).Page);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(response, IpcJson.Default.IpcResponse));
        Assert.False(doc.RootElement.TryGetProperty("lead_tokens", out _));
        Assert.False(doc.RootElement.GetProperty("page").GetProperty("jobs")[0].TryGetProperty("session_tokens", out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(home)) { Directory.Delete(home, recursive: true); }
    }
}
