using System.Text.Json.Nodes;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Backends;

public sealed class SandboxLaunchTests : IDisposable
{
    readonly TempStateDir _dir = new();

    public void Dispose() => _dir.Dispose();

    SandboxLaunch ClaudeSandbox() => SandboxLaunch.Create(_dir.Path, BackendCatalog.Claude, _dir.File("sandbox"), "job-1");

    [Fact]
    public void Claude_settings_file_is_written_by_atf_with_the_adr_policy()
    {
        var sandbox = ClaudeSandbox();

        var settings = JsonNode.Parse(File.ReadAllText(sandbox.ClaudeSettingsPath!))!;
        Assert.Equal(Path.Combine(_dir.File("sandbox"), "job-1.claude-settings.json"), sandbox.ClaudeSettingsPath);
        Assert.True((bool)settings["sandbox"]!["enabled"]!);
        Assert.True((bool)settings["sandbox"]!["autoAllowBashIfSandboxed"]!);
        Assert.False((bool)settings["sandbox"]!["allowUnsandboxedCommands"]!);
        Assert.True((bool)settings["sandbox"]!["failIfUnavailable"]!);
        Assert.Equal("~/.ssh", (string)settings["sandbox"]!["filesystem"]!["denyRead"]![0]!);
        Assert.True((bool)settings["disableAllHooks"]!);
        Assert.Contains("mcp__agentteamforge", settings["permissions"]!["allow"]!.AsArray().Select(n => (string)n!));
        Assert.Null(settings["sandbox"]!["network"]?["allowUnixSockets"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Claude_headless_arguments_branch_on_the_mode(bool sandboxed)
    {
        var args = ClaudeCodeBackend.Arguments(new BackendRequest("j", "c", "task", "model=opus") { Sandbox = sandboxed ? ClaudeSandbox() : null }, "s-1");

        if (sandboxed) { AssertSandboxedClaude(args); }
        else
        {
            Assert.Contains("--dangerously-skip-permissions", args);
            Assert.DoesNotContain("--setting-sources", args);
        }
        Assert.Equal("AskUserQuestion", args[args.IndexOf("--disallowed-tools") + 1]);
    }

    [Theory]
    [InlineData("herdr", false)]
    [InlineData("herdr", true)]
    [InlineData("tab", false)]
    [InlineData("tab", true)]
    public void Claude_interactive_arguments_branch_on_the_mode_for_every_launch_path(string path, bool sandboxed)
    {
        var launch = new InteractiveLaunch(InteractiveAgentKind.Claude, "atf0123456789abcdef01", Path.GetTempPath(), null, null, "/tmp/atf.launch.sh")
        { Sandbox = sandboxed ? ClaudeSandbox() : null };

        var args = (path == "herdr" ? HerdrAgentControl.AgentArguments(launch) : WtTabControl.AgentArguments(launch, "task", windowsCommandLine: false)).ToList();

        if (sandboxed)
        {
            AssertSandboxedClaude(args);
            Assert.DoesNotContain("bypassPermissions", args);
        }
        else
        {
            Assert.Equal("bypassPermissions", args[args.IndexOf("--permission-mode") + 1]);
            Assert.DoesNotContain("--setting-sources", args);
        }
        // The workspace-trust skip stays; it is not evidence of a sandbox (ADR 0013, guarantee 8).
        Assert.Equal(("CLAUDE_CODE_SANDBOXED", "1"), InteractiveAgentCommand.WorkspaceTrustEnvironment(InteractiveAgentKind.Claude));
    }

    static void AssertSandboxedClaude(List<string> args)
    {
        Assert.DoesNotContain("--dangerously-skip-permissions", args);
        Assert.Equal("dontAsk", args[args.IndexOf("--permission-mode") + 1]);
        var sources = args.IndexOf("--setting-sources");
        Assert.Equal("", args[sources + 1]);
        Assert.EndsWith(".claude-settings.json", args[args.IndexOf("--settings") + 1]);
        Assert.Contains("--strict-mcp-config", args);
    }

    [Theory]
    [InlineData("AWS_ACCESS_KEY_ID", true)]
    [InlineData("AWS_PROFILE", true)]
    [InlineData("GITHUB_TOKEN", true)]
    [InlineData("gh_token", true)]
    [InlineData("SSH_AUTH_SOCK", true)]
    [InlineData("GOOGLE_APPLICATION_CREDENTIALS", true)]
    [InlineData("MY_SERVICE_API_KEY", true)]
    [InlineData("NPM_TOKEN", true)]
    [InlineData("ANTHROPIC_API_KEY", false)]
    [InlineData("OPENAI_API_KEY", false)]
    [InlineData("CODEX_API_KEY", false)]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN", false)]
    [InlineData("PATH", false)]
    [InlineData("HOME", false)]
    public void Credential_variables_are_recognized_and_the_agents_own_sign_in_is_kept(string name, bool credential) =>
        Assert.Equal(credential, SandboxEnvironment.IsCredential(name));

    [Fact]
    public void Scrub_removes_only_credential_variables()
    {
        var environment = new Dictionary<string, string?> { ["AWS_SECRET_ACCESS_KEY"] = "x", ["GITHUB_TOKEN"] = "y", ["ANTHROPIC_API_KEY"] = "k", ["PATH"] = "/bin" };

        SandboxEnvironment.Scrub(environment);

        Assert.Equal(["ANTHROPIC_API_KEY", "PATH"], environment.Keys.Order());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Headless_launch_hands_a_sandboxed_agent_no_credential_variable(bool sandboxed)
    {
        const string name = "ATF_TEST_SCRUB_TOKEN";
        var script = _dir.File("claude-" + Guid.NewGuid().ToString("N"));
        var envFile = _dir.File("env-" + sandboxed);
        File.WriteAllText(script, $"#!/usr/bin/env bash\nprintenv > '{envFile}'\ncat > /dev/null\necho '{{\"type\":\"result\",\"result\":\"ok\"}}'\n".ReplaceLineEndings("\n"));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable(name, "secret");
        try
        {
            await using var run = new ClaudeCodeBackend(script).Start(new BackendRequest("j", "c", "task", "")
            { Sandbox = sandboxed ? ClaudeSandbox() : null, WorkingDirectory = _dir.Path });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await run.DeliverAsync(timeout.Token);
            await foreach (var _ in run.ReadEvidenceAsync(timeout.Token)) { }
        }
        finally { Environment.SetEnvironmentVariable(name, null); }

        Assert.Equal(!sandboxed, File.ReadAllText(envFile).Contains(name + "=secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sandbox_mode = \"danger-full-access\"", true)]
    [InlineData("default_permissions = \"open\"", true)]
    [InlineData("[sandbox_workspace_write]\nwritable_roots = [\"/\"]", true)]
    [InlineData("[permissions.open]\nx = 1", true)]
    [InlineData("[projects.\"/work/repo\"]\ntrust_level = \"trusted\"", true)]
    [InlineData("[projects.\"/work/repo\"]\ntrust_level = \"untrusted\"", false)]
    [InlineData("[projects.\"/elsewhere\"]\ntrust_level = \"trusted\"", false)]
    [InlineData("model = \"gpt-5\"\n[mcp_servers.x]\ncommand = \"y\"", false)]
    [InlineData("# sandbox_mode = \"danger-full-access\"", false)]
    public void Codex_user_config_that_widens_the_sandbox_is_detected(string toml, bool widens)
    {
        var path = _dir.File("config-" + Guid.NewGuid().ToString("N") + ".toml");
        File.WriteAllText(path, toml);

        Assert.Equal(widens, CodexConfigCheck.WidensSandbox("/work/repo/sub", path));
    }

    [Fact]
    public void A_missing_codex_config_does_not_widen_the_sandbox() =>
        Assert.False(CodexConfigCheck.WidensSandbox("/work/repo", _dir.File("absent.toml")));

    [Fact]
    public void Default_capability_downgrades_backends_without_a_sandbox_and_unverified_platforms()
    {
        Assert.Equal(SafetyDowngrade.UnsupportedBackend, SandboxCapability.Default(BackendCatalog.Pi, null));
        Assert.Equal(SafetyDowngrade.UnsupportedBackend, SandboxCapability.Default(BackendCatalog.Cursor, null));
        Assert.Equal(SafetyDowngrade.UnsupportedBackend, SandboxCapability.Default(BackendCatalog.Droid, null));
        Assert.Equal(OperatingSystem.IsLinux() ? null : SafetyDowngrade.Unavailable, SandboxCapability.Default(BackendCatalog.Claude, null));
    }
}
