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
    [InlineData("AWS_ACCESS_KEY_ID", false)]
    [InlineData("GITHUB_TOKEN", false)]
    [InlineData("SSH_AUTH_SOCK", false)]
    [InlineData("GOOGLE_APPLICATION_CREDENTIALS", false)]
    [InlineData("A_CREDENTIAL_NOBODY_LISTED", false)]
    [InlineData("INTERNAL_BUILD_PASSPHRASE", false)]
    [InlineData("ANTHROPIC_API_KEY", true)]
    [InlineData("OPENAI_API_KEY", true)]
    [InlineData("CODEX_API_KEY", true)]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN", true)]
    [InlineData("PATH", true)]
    [InlineData("HOME", true)]
    [InlineData("LC_ALL", true)]
    public void Only_listed_variables_reach_a_sandboxed_agent(string name, bool allowed) =>
        Assert.Equal(allowed, SandboxEnvironment.IsAllowed(name));

    [Fact]
    public void Restrict_keeps_only_the_allow_list_so_an_unlisted_credential_is_dropped()
    {
        var environment = new Dictionary<string, string?> { ["A_CREDENTIAL_NOBODY_LISTED"] = "x", ["AWS_SECRET_ACCESS_KEY"] = "y", ["ANTHROPIC_API_KEY"] = "k", ["PATH"] = "/bin" };

        SandboxEnvironment.Restrict(environment);

        Assert.Equal(["ANTHROPIC_API_KEY", "PATH"], environment.Keys.Order());
    }

    [Fact]
    public async Task The_terminal_prologue_unsets_every_unlisted_variable_in_a_real_shell()
    {
        var info = new System.Diagnostics.ProcessStartInfo("sh") { RedirectStandardOutput = true, UseShellExecute = false };
        info.Environment.Clear();
        foreach (var (name, value) in new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["HOME"] = "/home/x", ["LC_ALL"] = "C", ["A_CREDENTIAL_NOBODY_LISTED"] = "secret", ["GH_TOKEN"] = "t", ["ANTHROPIC_API_KEY"] = "k" })
        {
            info.Environment[name] = value;
        }
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(SandboxEnvironment.ShellPrologue() + "env");
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Contains("HOME=/home/x", output);
        Assert.Contains("LC_ALL=C", output);
        Assert.Contains("ANTHROPIC_API_KEY=k", output);
        Assert.DoesNotContain("A_CREDENTIAL_NOBODY_LISTED", output);
        Assert.DoesNotContain("GH_TOKEN", output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Headless_launch_hands_a_sandboxed_agent_only_listed_variables(bool sandboxed)
    {
        const string name = "ATF_TEST_UNLISTED_CREDENTIAL";
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

        var environment = File.ReadAllText(envFile);
        Assert.Equal(!sandboxed, environment.Contains(name + "=secret", StringComparison.Ordinal));
        Assert.Contains("PATH=", environment);
    }

    [Fact]
    public void Default_capability_downgrades_backends_without_a_sandbox_and_unverified_platforms()
    {
        Assert.Equal(SafetyDowngrade.UnsupportedBackend, SandboxCapability.Default(BackendCatalog.Pi, null, null));
        Assert.Equal(SafetyDowngrade.UnsupportedBackend, SandboxCapability.Default(BackendCatalog.Cursor, null, null));
        Assert.Equal(SafetyDowngrade.UnsupportedBackend, SandboxCapability.Default(BackendCatalog.Droid, null, null));
        Assert.Equal(OperatingSystem.IsLinux() ? null : SafetyDowngrade.Unavailable, SandboxCapability.Default(BackendCatalog.Claude, null, null));
    }

    [Theory]
    [InlineData("herdr")]
    [InlineData("terminal")]
    [InlineData("wt")]
    public void An_interactive_launch_is_never_sandboxed_because_its_pane_outlives_the_turn_and_its_environment_is_not_ours(string launchMode)
    {
        // Herdr starts the canonical executable in a shell whose startup files run first, and no launch path proves when a pane ends.
        Assert.Equal(SafetyDowngrade.Unavailable, SandboxCapability.Default(BackendCatalog.Claude, null, launchMode));
        Assert.Equal(SafetyDowngrade.Unavailable, SandboxCapability.Default(BackendCatalog.Codex, null, launchMode));
        Assert.Equal(OperatingSystem.IsLinux() ? null : SafetyDowngrade.Unavailable, SandboxCapability.Default(BackendCatalog.Codex, null, "headless"));
    }
}
