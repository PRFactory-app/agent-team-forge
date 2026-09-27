using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.External;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.External;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Features.Sessions;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class ManagedChildContextTests
{
    [Fact]
    public void Prfactory_child_context_hides_unanswerable_human_input()
    {
        using var f = new JobFixture();
        var root = Path.GetDirectoryName(f.DatabasePath)!;
        var lead = new LeadSessionStore(f.Database).Start(root, "parent");
        var team = new ExternalTeam(new ExternalMemberStore(f.Database), new WakeStore(f.Database));
        var principal = new BoundPrincipal("prfactory", "connector", "connector-lead");
        var accept = new AcceptJob(f.Store, principal, f.Limits, f.TestProfile, f.Admission, ["codex"]);
        var job = accept.Execute(new SubmitJobRequest("prf:question", "task", null, false)
        { Backend = "codex", LeadSessionId = lead.SessionId }).Job!;
        var context = new ManagedChildContext(f.Store, team, root, "/private/atf");

        var prepared = context.Prepare(new BackendRequest(job.JobId, "correlation", "task", "{}"));
        var args = JsonNode.Parse(File.ReadAllText(prepared.ManagedMcpConfig!))!["mcpServers"]![ManagedChildContext.ServerName]!["args"]!.AsArray();
        var saved = JsonNode.Parse(File.ReadAllText(args[4]!.GetValue<string>()))!;
        Assert.False(saved["human_input_available"]!.GetValue<bool>());
    }

    [Fact]
    public void Local_child_hides_human_input_and_direct_call_returns_guidance()
    {
        using var f = new JobFixture();
        var root = Path.GetDirectoryName(f.DatabasePath)!;
        var lead = new LeadSessionStore(f.Database).Start(root, "parent");
        var team = new ExternalTeam(new ExternalMemberStore(f.Database), new WakeStore(f.Database));
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, f.TestProfile, f.Admission, ["codex"]);
        var job = accept.Execute(new SubmitJobRequest("local-question", "task", null, false)
        { Backend = "codex", LeadSessionId = lead.SessionId }).Job!;
        var context = new ManagedChildContext(f.Store, team, root, "/private/atf");

        var prepared = context.Prepare(new BackendRequest(job.JobId, "correlation", "task", "{}"));
        var args = JsonNode.Parse(File.ReadAllText(prepared.ManagedMcpConfig!))!["mcpServers"]![ManagedChildContext.ServerName]!["args"]!.AsArray();
        var saved = JsonNode.Parse(File.ReadAllText(args[4]!.GetValue<string>()))!;
        var token = saved["member_token"]!.GetValue<string>();

        Assert.False(saved["human_input_available"]!.GetValue<bool>());
        Assert.False(JobsMcpBridge.ShouldOfferHumanInput(token, saved["human_input_available"]!.GetValue<bool>()));
        var asked = PRFactoryInteraction.RequestFromManagedChild(team.ManagedChildName(token));
        Assert.Equal(PRFactoryInteraction.HumanInputUnavailable, asked.Error);
    }

    [Fact]
    public async Task Lead_bound_pi_without_adapter_fails_before_launch_and_without_fence()
    {
        using var f = new JobFixture();
        var root = Path.GetDirectoryName(f.DatabasePath)!;
        var home = Path.Combine(root, "home");
        var lead = new LeadSessionStore(f.Database).Start(root, "parent");
        var team = new ExternalTeam(new ExternalMemberStore(f.Database), new WakeStore(f.Database));
        var context = new ManagedChildContext(f.Store, team, root, "/private/atf");
        var backend = new ScriptedBackend(r => [new BackendEvidence.Result(r.Correlation, "done")]);
        var catalog = new BackendCatalog().Register(BackendCatalog.Pi, () => backend);
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, f.TestProfile, f.Admission, catalog.Names);
        var first = accept.Execute(new SubmitJobRequest("first", "task", null, false)
        { Backend = BackendCatalog.Pi, LeadSessionId = lead.SessionId }).Job!;
        using var dispatch = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { },
            childContext: context, piHome: home);

        await dispatch.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);

        var failed = f.Store.GetJob(first.JobId)!;
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Equal("pi_mcp_adapter_missing", failed.ReasonCode);
        Assert.Contains("pi install npm:pi-mcp-adapter", failed.ResultText);
        Assert.False(f.Store.IsSessionFenced(first.JobId));
        Assert.Empty(backend.Started);

        var settings = Path.Combine(home, ".pi", "agent", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, "{\"packages\":[\"npm:pi-mcp-adapter\"]}");
        var second = accept.Execute(new SubmitJobRequest("second", "task", null, false)
        { Backend = BackendCatalog.Pi, LeadSessionId = lead.SessionId }).Job!;
        await dispatch.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        Assert.Equal(JobStatus.Completed, f.Store.GetJob(second.JobId)!.Status);
        Assert.Single(backend.Started);
    }

    [Fact]
    public async Task Spawn_and_resume_receive_private_configuration_for_the_same_parent()
    {
        using var f = new JobFixture();
        var root = Path.GetDirectoryName(f.DatabasePath)!;
        var lead = new LeadSessionStore(f.Database).Start(root, "parent");
        var team = new ExternalTeam(new ExternalMemberStore(f.Database), new WakeStore(f.Database));
        var context = new ManagedChildContext(f.Store, team, root, "/private/atf");
        var backend = new ScriptedBackend(r =>
        [
            new BackendEvidence.Session(r.Correlation, "native-child"),
            new BackendEvidence.Result(r.Correlation, "done"),
        ]);
        var catalog = new BackendCatalog().Register("claude", () => backend);
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, f.TestProfile, f.Admission, catalog.Names);
        var first = accept.Execute(new SubmitJobRequest("first", "task", null, false) { Backend = "claude", LeadSessionId = lead.SessionId }).Job!;
        using var dispatch = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, f.Admission, _ => { }, childContext: context);
        await dispatch.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        var next = new FollowUpJob(f.Store, JobFixture.Operator, accept).Execute(new FollowUpRequest(first.JobId, "again", "next") { LeadSessionId = lead.SessionId });
        Assert.Null(next.Error);
        await dispatch.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        Assert.Equal(2, backend.Started.Count);
        Assert.Equal("native-child", backend.Started[1].ResumeSessionId);
        Assert.Equal(File.ReadAllText(backend.Started[0].ManagedMcpConfig!), File.ReadAllText(backend.Started[1].ManagedMcpConfig!));
        var configPath = backend.Started[0].ManagedMcpConfig!;
        var args = JsonNode.Parse(File.ReadAllText(configPath))!["mcpServers"]![ManagedChildContext.ServerName]!["args"]!.AsArray();
        Assert.Equal(root, args[2]!.GetValue<string>());
        var contextPath = args[4]!.GetValue<string>();
        var saved = JsonNode.Parse(File.ReadAllText(contextPath))!;
        Assert.Equal(lead.SessionId, saved["parent_session_id"]!.GetValue<string>());
        Assert.Equal("child-" + first.JobId, team.ManagedChildName(saved["member_token"]!.GetValue<string>()));
        var foreignTeam = team.CreateActorTeam("foreign")!;
        var forged = team.CreateTicketForTeam(foreignTeam, "child-" + first.JobId, null).Ticket!;
        var foreignToken = team.Join(foreignTeam, forged.Token).Member!.MemberToken;
        Assert.Null(team.ManagedChildName(foreignToken));
        Assert.True(team.Send(saved["member_token"]!.GetValue<string>(), "report").Ok);
        Assert.Equal("report", Assert.Single(team.ReadLead(lead.SessionId, root, null, null).Inbox!.Messages).Text);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(contextPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(configPath));
        }
        foreach (var request in backend.Started)
        {
            Assert.Contains(request.ManagedMcpConfig!, ClaudeCodeBackend.Arguments(request, "session"));
            Assert.Contains(request.ManagedMcpConfig!, PiBackend.BuildArguments(request));
            Assert.Contains(CodexExecBackend.BuildArguments(request), arg => arg.StartsWith("mcp_servers.agentteamforge.args=", StringComparison.Ordinal)
                && arg.Contains(contextPath, StringComparison.Ordinal));
            var launch = new InteractiveLaunch(InteractiveAgentKind.Claude, "atf-test", root, request.ResumeSessionId, null,
                Path.Combine(root, "herdr", "atf-test.bootstrap"))
            { JobId = request.JobId };
            Assert.Contains(request.ManagedMcpConfig!, HerdrAgentControl.AgentArguments(launch));
        }
        Assert.True(team.CloseTeam(lead.SessionId));
        Assert.Null(team.ManagedChildName(saved["member_token"]!.GetValue<string>()));
        Assert.Equal("membership_revoked", team.Send(saved["member_token"]!.GetValue<string>(), "late report").Error);
    }

    [Fact]
    public void Child_process_does_not_inherit_parent_messaging_or_native_identity()
    {
        var info = new ProcessStartInfo();
        info.Environment.Clear();
        info.Environment["WIN_AGENT_TEAMS_PARENT_ID"] = "parent";
        info.Environment["CLAUDE_CODE_MESSAGING_SOCKET"] = "/parent/socket";
        info.Environment["CLAUDE_CODE_ENTRYPOINT"] = "parent";
        info.Environment["CLAUDE_CODE_SSE_PORT"] = "1234";
        info.Environment["CLAUDE_CODE_SESSION_ID"] = "parent-session";
        info.Environment["CLAUDE_CODE_PARENT_SESSION_ID"] = "parent-session";
        info.Environment["CLAUDE_CODE_SESSION_ATTENDED"] = "0";
        info.Environment["CLAUDE_CODE_REMOTE_SESSION_ID"] = "parent-remote";
        info.Environment["CLAUDE_CODE_BRIDGE_SESSION_ID"] = "parent-bridge";
        info.Environment["CLAUDE_CODE_WEBSOCKET_AUTH_FILE_DESCRIPTOR"] = "3";
        info.Environment["AI_AGENT"] = "claude-code_agent";
        info.Environment["CODEX_THREAD_ID"] = "parent-thread";
        info.Environment["HERDR_PANE_ID"] = "parent-pane";
        info.Environment["ATF_EXTERNAL_ONLY"] = "1";
        info.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = "test-credential";
        info.Environment["CLAUDE_CODE_GIT_BASH_PATH"] = "C:\\Git\\bin\\bash.exe";
        info.Environment["CLAUDE_CODE_USE_BEDROCK"] = "1";
        info.Environment["CLAUDE_CODE_USE_VERTEX"] = "1";
        info.Environment["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = "2048";
        info.Environment["CLAUDE_CODE_SANDBOXED"] = "1";
        info.Environment["AWS_PROFILE"] = "team";
        info.Environment["GOOGLE_APPLICATION_CREDENTIALS"] = "/tmp/gcp.json";
        ManagedChildContext.ClearInheritedIdentity(info);
        string[] expected =
        [
            "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_GIT_BASH_PATH", "CLAUDE_CODE_USE_BEDROCK",
            "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_MAX_OUTPUT_TOKENS", "CLAUDE_CODE_SANDBOXED",
            "AWS_PROFILE", "GOOGLE_APPLICATION_CREDENTIALS",
        ];
        Assert.Equal(expected.Order(), info.Environment.Keys.Order());
    }
}
