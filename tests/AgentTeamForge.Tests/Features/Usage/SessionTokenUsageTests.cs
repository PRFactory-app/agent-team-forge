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
        Assert.Equal(7, reader.Read("claude", "claude-session", home)!.Input);
    }

    static string Codex(long input, long cached, long output) => $$$$$"""
        {"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":{{{{{input}}}}},"cached_input_tokens":{{{{{cached}}}}},"output_tokens":{{{{{output}}}}}}}}}
        """ + "\n";
    [Fact]
    public void Codex_uses_last_cumulative_value_and_excludes_cached_input()
    {
        var path = Transcript("codex", "thread", "{\"type\":\"session_meta\",\"payload\":{\"id\":\"thread\",\"source\":\"cli\"}}\n"
            + Codex(100, 40, 10) + Codex(200, 80, 30));
        Assert.Equal(new TokenUsage(120, 30, 80, 0), reader.Read("codex", "thread", home));
        File.AppendAllText(path, Codex(300, 90, 40));
        Assert.Equal(340, reader.Read("codex", "thread", home)!.Total);
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
