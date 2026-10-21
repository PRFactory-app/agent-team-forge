using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Backends;

public sealed class CodexSandboxArgumentTests : IDisposable
{
    readonly TempStateDir _dir = new();

    public void Dispose() => _dir.Dispose();

    static SandboxLaunch Sandbox => new("/repo/.git", "/repo/.git/worktrees/job-1", null) { DenyRead = ["~/.ssh"] };

    string ManagedConfig()
    {
        var path = _dir.File("mcp.json");
        File.WriteAllText(path, """{"mcpServers":{"agentteamforge":{"command":"/bin/atf","args":["mcp"]}}}""");
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Full_access_exec_keeps_the_bypass_flags_and_has_no_sandbox_profile(bool resume)
    {
        var args = CodexExecBackend.BuildArguments(new BackendRequest("j", "c", "task", "model=gpt-x") { ResumeSessionId = resume ? "thread-1" : null });

        Assert.Equal(resume ? ["exec", "resume"] : ["exec"], args.Take(resume ? 2 : 1));
        Assert.Contains("--dangerously-bypass-approvals-and-sandbox", args);
        Assert.DoesNotContain("--ignore-user-config", args);
        Assert.DoesNotContain(args, a => a.StartsWith("permissions.", StringComparison.Ordinal) || a.StartsWith("default_permissions", StringComparison.Ordinal));
        Assert.Equal("-", args[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sandboxed_exec_replaces_the_bypass_flag_with_the_permission_profile(bool resume)
    {
        var args = CodexExecBackend.BuildArguments(new BackendRequest("j", "c", "task", "model=gpt-x") { Sandbox = Sandbox, ResumeSessionId = resume ? "thread-1" : null });

        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", args);
        Assert.DoesNotContain("-s", args);
        Assert.DoesNotContain("-a", args);
        Assert.Contains("--skip-git-repo-check", args);
        // The user config (sandbox keys, project trust) and execpolicy rules never apply to a sandboxed exec.
        Assert.Contains("--ignore-user-config", args);
        Assert.Contains("--ignore-rules", args);
        Assert.Contains("default_permissions='atf'", args);
        Assert.Contains("permissions.atf.network.enabled=false", args);
        var filesystem = args.Single(a => a.StartsWith("permissions.atf.filesystem=", StringComparison.Ordinal));
        Assert.Contains("'/'='read'", filesystem);
        Assert.Contains("':workspace_roots'='write'", filesystem);
        Assert.Contains("'~/.ssh'='none'", filesystem);
        Assert.Contains("'/repo/.git/worktrees/job-1'='write'", filesystem);
        foreach (var name in new[] { "objects", "refs", "logs" }) { Assert.Contains($"'/repo/.git/{name}'='write'", filesystem); }
        // Hooks and config are never writable (ADR 0013).
        Assert.DoesNotContain("/repo/.git/hooks", filesystem);
        Assert.DoesNotContain("'/repo/.git/config'", filesystem);
        Assert.DoesNotContain("trust_level", string.Join(' ', args));
        Assert.Equal("-", args[^1]);
    }

    [Fact]
    public void Sandboxed_exec_approves_the_managed_mcp_server_only_when_it_is_configured()
    {
        var without = CodexExecBackend.BuildArguments(new BackendRequest("j", "c", "task", "") { Sandbox = Sandbox });
        var with = CodexExecBackend.BuildArguments(new BackendRequest("j", "c", "task", "") { Sandbox = Sandbox, ManagedMcpConfig = ManagedConfig() });

        Assert.DoesNotContain(without, a => a.Contains("default_tools_approval_mode", StringComparison.Ordinal));
        Assert.Contains("mcp_servers.agentteamforge.default_tools_approval_mode='approve'", with);
    }

    [Fact]
    public void Sandboxed_exec_in_a_plain_checkout_writes_only_objects_refs_and_logs()
    {
        var args = CodexExecBackend.BuildArguments(new BackendRequest("j", "c", "task", "") { Sandbox = new SandboxLaunch("/repo/.git", "/repo/.git", null) });
        var filesystem = args.Single(a => a.StartsWith("permissions.atf.filesystem=", StringComparison.Ordinal));

        Assert.DoesNotContain("'/repo/.git'='write'", filesystem);
        Assert.Contains("'/repo/.git/objects'='write'", filesystem);
    }

    [Fact]
    public void Paths_with_a_single_quote_use_an_escaped_basic_string()
    {
        var filesystem = new SandboxLaunch("/it's/.git", "/it's/.git/worktrees/w", null).CodexArguments(false)[1];

        Assert.Contains("\"/it's/.git/objects\"='write'", filesystem);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Interactive_codex_arguments_branch_on_the_mode(bool sandboxed, bool resume)
    {
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atf0123456789abcdef01", Path.GetTempPath(), resume ? "thread-1" : null, null, "/tmp/atf.launch.sh")
        { Sandbox = sandboxed ? Sandbox : null };

        var args = InteractiveAgentCommand.ManagedArguments(launch).ToList();

        if (sandboxed)
        {
            Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", args);
            Assert.Equal("never", args[args.IndexOf("-a") + 1]);
            Assert.Equal(Path.GetTempPath(), args[args.IndexOf("-C") + 1]);
            Assert.Contains("default_permissions='atf'", args);
            Assert.DoesNotContain(args, a => a.StartsWith("projects=", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains("--dangerously-bypass-approvals-and-sandbox", args);
            Assert.DoesNotContain("-a", args);
            Assert.Contains(args, a => a.StartsWith("projects=", StringComparison.Ordinal));
        }
        Assert.Contains(args, a => a.Contains("ATF_RUN_CORRELATION", StringComparison.Ordinal) || a.Contains("atf0123456789abcdef01", StringComparison.Ordinal));
        if (resume) { Assert.Equal(["resume", "thread-1"], args.TakeLast(2)); }
    }

    [Fact]
    public void Sandboxed_codex_launches_through_herdr_and_the_terminal_wrappers_share_the_arguments()
    {
        var launch = new InteractiveLaunch(InteractiveAgentKind.Codex, "atf0123456789abcdef01", Path.GetTempPath(), null, null, "/tmp/atf.launch.sh") { Sandbox = Sandbox };

        var herdr = HerdrAgentControl.AgentArguments(launch);

        Assert.Equal(InteractiveAgentCommand.ManagedArguments(launch), herdr);
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", herdr);
    }
}
