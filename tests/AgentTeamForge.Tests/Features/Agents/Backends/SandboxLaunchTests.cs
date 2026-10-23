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

    /// <summary>A workspace beside the test homes: a cache inside the workspace is dropped (the agent can write its parents).</summary>
    string Workspace => Directory.CreateDirectory(_dir.File("workspace")).FullName;

    SandboxLaunch ClaudeSandbox() => SandboxLaunch.Create(_dir.Path, BackendCatalog.Claude, _dir.File("sandbox"), "job-1", Home("ssh"));

    string Home(params string[] present)
    {
        var home = Directory.CreateDirectory(_dir.File("home-" + string.Join("-", present))).FullName;
        foreach (var name in present)
        {
            var path = Path.Combine(home, name switch { "ssh" => ".ssh", "aws" => ".aws", "gh" => ".config/gh", _ => ".netrc" });
            if (name == "netrc") { File.WriteAllText(path, "machine x"); } else { Directory.CreateDirectory(path); }
        }
        return home;
    }

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
        Assert.Equal(["~/.ssh"], settings["sandbox"]!["filesystem"]!["denyRead"]!.AsArray().Select(n => (string)n!));
        Assert.True((bool)settings["disableAllHooks"]!);
        Assert.Contains("mcp__agentteamforge", settings["permissions"]!["allow"]!.AsArray().Select(n => (string)n!));
        Assert.Null(settings["sandbox"]!["network"]?["allowUnixSockets"]);
        // Network stays on by default: Claude's proxy reaches the listed git hosts and package registries.
        var domains = settings["sandbox"]!["network"]!["allowedDomains"]!.AsArray().Select(n => (string)n!).ToList();
        Assert.Contains("github.com", domains);
        Assert.Contains("api.nuget.org", domains);
        Assert.DoesNotContain("*", domains); // Claude rejects a bare "*" in allowedDomains.
    }

    [Fact]
    public void Default_cache_paths_that_exist_are_writable_for_both_clis_and_missing_ones_are_skipped()
    {
        var home = Home("ssh");
        var npm = Directory.CreateDirectory(Path.Combine(home, ".npm")).FullName;
        var nuget = Directory.CreateDirectory(Path.Combine(home, ".nuget", "packages")).FullName;

        var claude = SandboxLaunch.Create(Workspace, BackendCatalog.Claude, _dir.File("sandbox"), "job-caches", home);
        var codex = SandboxLaunch.Create(Workspace, BackendCatalog.Codex, _dir.File("sandbox"), "job-caches-codex", home).CodexArguments(false)[1];

        var allowWrite = JsonNode.Parse(File.ReadAllText(claude.ClaudeSettingsPath!))!["sandbox"]!["filesystem"]!["allowWrite"]!.AsArray()
            .Select(n => (string)n!).ToList();
        Assert.Contains(npm, allowWrite);
        Assert.Contains(nuget, allowWrite);
        Assert.DoesNotContain(Path.Combine(home, ".cargo", "registry"), allowWrite); // Not on this machine: skipped, not created.
        Assert.False(Directory.Exists(Path.Combine(home, ".cargo")));
        Assert.Contains($"'{npm}'='write'", codex);
        Assert.Contains($"'{nuget}'='write'", codex);
    }

    [Fact]
    public void Custom_domains_and_cache_paths_reach_the_claude_settings_and_the_codex_profile()
    {
        var home = Home();
        var cache = Directory.CreateDirectory(Path.Combine(home, "build-cache")).FullName;
        var settings = new SandboxSettings(true, ["example.com", "*.example.org"], ["~/build-cache"]);

        var claude = SandboxLaunch.Create(Workspace, BackendCatalog.Claude, _dir.File("sandbox"), "job-custom", home, settings: settings);
        var codex = SandboxLaunch.Create(Workspace, BackendCatalog.Codex, _dir.File("sandbox"), "job-custom-codex", home, settings: settings).CodexArguments(false);

        var json = JsonNode.Parse(File.ReadAllText(claude.ClaudeSettingsPath!))!["sandbox"]!;
        Assert.Equal(["example.com", "*.example.org"], json["network"]!["allowedDomains"]!.AsArray().Select(n => (string)n!));
        Assert.Equal([cache], json["filesystem"]!["allowWrite"]!.AsArray().Select(n => (string)n!));
        Assert.Contains($"'{cache}'='write'", codex[1]);
        Assert.Contains("permissions.atf.network.enabled=true", codex); // Codex stays unrestricted: the domain list is Claude's.
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*.*")]
    [InlineData("*.com")]
    [InlineData("https://github.com")]
    [InlineData("github.com/org")]
    [InlineData("github.com:443")]
    [InlineData("api.*.com")]
    [InlineData("GitHub.com")]
    [InlineData("")]
    public void Domain_validation_refuses_entries_claude_rejects_or_that_are_too_broad(string domain) =>
        Assert.NotNull(SandboxPolicy.DomainError(domain));

    [Theory]
    [InlineData("github.com")]
    [InlineData("*.github.com")]
    [InlineData("registry.npmjs.org")]
    public void Domain_validation_accepts_hosts_and_subdomain_patterns(string domain) => Assert.Null(SandboxPolicy.DomainError(domain));

    [Theory]
    [InlineData("~/.ssh")]
    [InlineData("~/.ssh/keys")]
    [InlineData("~/.aws")]
    [InlineData("~/.config/gh")]
    [InlineData("~/.config")] // A parent of ~/.config/gh.
    [InlineData("~")]
    [InlineData("/")]
    [InlineData("relative/cache")]
    [InlineData("~/.netrc")]
    public void Cache_path_validation_refuses_secrets_their_parents_and_broad_paths(string entry) =>
        Assert.NotNull(SandboxPolicy.CachePathError(entry, Home()));

    [Fact]
    public void Cache_path_validation_refuses_the_state_directory()
    {
        var home = Home();
        Assert.NotNull(SandboxPolicy.CachePathError("~/.local/share", home, Path.Combine(home, ".local", "share", "atf")));
        Assert.Null(SandboxPolicy.CachePathError("~/.local/share/NuGet", home, Path.Combine(home, ".local", "share", "atf")));
    }

    [Fact]
    public void A_hand_edited_secret_or_a_link_into_one_never_becomes_writable_at_launch()
    {
        var home = Home("ssh", "aws");
        var link = Path.Combine(home, "cache-link");
        Directory.CreateSymbolicLink(link, Path.Combine(home, ".ssh"));
        Directory.CreateDirectory(Path.Combine(home, ".npm"));
        // A profile edited by hand skips the web console's validation; the launch checks again.
        var settings = new SandboxSettings(true, ["*", "github.com"], ["~/.ssh", "~/.aws", "~/cache-link", "~", "~/.npm"]);

        var claude = SandboxLaunch.Create(Workspace, BackendCatalog.Claude, _dir.File("sandbox"), "job-deny", home, settings: settings);
        var codex = SandboxLaunch.Create(Workspace, BackendCatalog.Codex, _dir.File("sandbox"), "job-deny-codex", home, settings: settings).CodexArguments(false)[1];

        var json = JsonNode.Parse(File.ReadAllText(claude.ClaudeSettingsPath!))!["sandbox"]!;
        Assert.Equal([Path.Combine(home, ".npm")], json["filesystem"]!["allowWrite"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(["github.com"], json["network"]!["allowedDomains"]!.AsArray().Select(n => (string)n!));
        Assert.Contains("'~/.ssh'='none'", codex);
        Assert.DoesNotContain(".ssh'='write'", codex);
        Assert.DoesNotContain("cache-link", codex);
        Assert.DoesNotContain($"'{home}'='write'", codex);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("target/keys")]
    [InlineData("")] // The parent of the target.
    public void A_cache_at_or_around_the_target_of_a_symlinked_secret_is_refused_and_never_granted(string relative)
    {
        var home = Home();
        var data = Directory.CreateDirectory(_dir.File("data")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(data, "target", "keys")).Parent!.FullName;
        Directory.CreateSymbolicLink(Path.Combine(home, ".ssh"), target);
        var cache = relative.Length == 0 ? data : Path.Combine(data, relative);

        Assert.NotNull(SandboxPolicy.CachePathError(cache, home));
        var settings = new SandboxSettings(true, ["github.com"], [cache]);
        var claude = SandboxLaunch.Create(_dir.Path, BackendCatalog.Claude, _dir.File("sandbox"), "job-link-" + relative.Length, home, settings: settings);
        var codex = SandboxLaunch.Create(_dir.Path, BackendCatalog.Codex, _dir.File("sandbox"), "job-link-codex-" + relative.Length, home, settings: settings);
        Assert.Empty(JsonNode.Parse(File.ReadAllText(claude.ClaudeSettingsPath!))!["sandbox"]!["filesystem"]!["allowWrite"]!.AsArray());
        Assert.Empty(codex.CachePaths);
        Assert.DoesNotContain(data, codex.CodexArguments(false)[1]);
    }

    [Fact]
    public void A_cache_under_the_target_of_a_symlinked_config_parent_or_home_is_refused()
    {
        var real = Directory.CreateDirectory(_dir.File("real-config")).FullName;
        var home = Home();
        Directory.CreateSymbolicLink(Path.Combine(home, ".config"), real); // ~/.config/gh is then real-config/gh.
        Assert.NotNull(SandboxPolicy.CachePathError(Path.Combine(real, "gh", "cache"), home));

        var realHome = Directory.CreateDirectory(_dir.File("real-home")).FullName;
        var linkedHome = _dir.File("linked-home");
        Directory.CreateSymbolicLink(linkedHome, realHome);
        Assert.NotNull(SandboxPolicy.CachePathError(Path.Combine(realHome, ".aws"), linkedHome)); // The physical ~/.aws.
        Assert.NotNull(SandboxPolicy.CachePathError(realHome, linkedHome)); // The physical home.
        Assert.Null(SandboxPolicy.CachePathError(Path.Combine(realHome, ".npm"), linkedHome));
    }

    [Fact]
    public void A_cache_under_the_target_of_a_symlinked_state_directory_is_refused()
    {
        var home = Home();
        var real = Directory.CreateDirectory(_dir.File("real-state")).FullName;
        var state = _dir.File("state-link");
        Directory.CreateSymbolicLink(state, real);

        Assert.NotNull(SandboxPolicy.CachePathError(Path.Combine(real, "sandbox"), home, state));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/bad\0path")]
    [InlineData("~/bad\0path")]
    public void A_null_or_nul_cache_entry_is_a_validation_error_not_an_exception(string? entry)
    {
        Assert.NotNull(SandboxPolicy.CachePathError(entry, Home()));
        Assert.Empty(SandboxPolicy.ResolveCachePaths([entry], Home()));
    }

    [Fact]
    public void A_null_domain_is_a_validation_error_not_an_exception() => Assert.NotNull(SandboxPolicy.DomainError(null));

    [Fact]
    public void A_cache_link_retargeted_after_the_launch_is_built_does_not_move_the_grant()
    {
        var home = Home("ssh");
        var safe = Directory.CreateDirectory(Path.Combine(home, "real-cache")).FullName;
        var alias = Path.Combine(home, "cache");
        Directory.CreateSymbolicLink(alias, safe);
        var settings = new SandboxSettings(true, [], ["~/cache"]);

        var claude = SandboxLaunch.Create(Workspace, BackendCatalog.Claude, _dir.File("sandbox"), "job-retarget", home, settings: settings);
        var codex = SandboxLaunch.Create(Workspace, BackendCatalog.Codex, _dir.File("sandbox"), "job-retarget-codex", home, settings: settings);
        Directory.Delete(alias); // Another agent retargets the alias at ~/.ssh before the CLI starts its sandbox.
        Directory.CreateSymbolicLink(alias, Path.Combine(home, ".ssh"));

        // Both grants name the validated physical target, so the retargeted link is not writable.
        Assert.Equal([safe], JsonNode.Parse(File.ReadAllText(claude.ClaudeSettingsPath!))!["sandbox"]!["filesystem"]!["allowWrite"]!.AsArray().Select(n => (string)n!));
        Assert.Equal([safe], codex.CachePaths);
        Assert.Contains($"'{safe}'='write'", codex.CodexArguments(false)[1]);
        Assert.DoesNotContain($"'{alias}'", codex.CodexArguments(false)[1]);
    }

    [Fact]
    public void A_cache_link_retargeted_between_check_and_grant_is_granted_as_the_checked_target()
    {
        var home = Home("ssh");
        var safe = Directory.CreateDirectory(Path.Combine(home, "real-cache")).FullName;
        var alias = Path.Combine(home, "cache");
        Directory.CreateSymbolicLink(alias, safe);
        var resolutions = 0;
        // Each cache resolution sees the link as it is, then another process retargets it at ~/.ssh: a second resolution would find ~/.ssh.
        SandboxPolicy.CacheResolver.Value = path =>
        {
            resolutions++;
            var resolved = PhysicalPath.Resolve(path);
            Directory.Delete(alias);
            Directory.CreateSymbolicLink(alias, Path.Combine(home, ".ssh"));
            return resolved;
        };
        try
        {
            Assert.Equal([safe], SandboxPolicy.ResolveCachePaths(["~/cache"], home));
            Assert.Equal(1, resolutions);
        }
        finally { SandboxPolicy.CacheResolver.Value = null; }
    }

    [Fact]
    public void A_cache_inside_the_workspace_or_inside_another_cache_is_not_granted_separately()
    {
        var home = Home();
        var outer = Directory.CreateDirectory(Path.Combine(home, "outer")).FullName;
        Directory.CreateDirectory(Path.Combine(outer, "inner"));
        var inWorkspace = Directory.CreateDirectory(Path.Combine(Workspace, "cache")).FullName;

        var caches = SandboxPolicy.ResolveCachePaths(["~/outer", "~/outer/inner", inWorkspace], home, writableRoots: [Workspace]);

        Assert.Equal([outer], caches); // The agent can write the parents of the other two and swap a component for a link.
    }

    [Fact]
    public void The_per_machine_opt_in_turns_claude_network_off()
    {
        var sandbox = SandboxLaunch.Create(_dir.Path, BackendCatalog.Claude, _dir.File("sandbox"), "job-off", Home("ssh"),
            settings: SandboxSettings.Default with { Network = false });

        var settings = JsonNode.Parse(File.ReadAllText(sandbox.ClaudeSettingsPath!))!;
        Assert.False(sandbox.Network);
        Assert.Null(settings["sandbox"]!["network"]); // No domain listed: the proxy denies every request under dontAsk.
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

    [Fact]
    public void Deny_read_covers_every_existing_secret_location_and_skips_the_ones_that_do_not_exist()
    {
        Assert.Equal(["~/.ssh", "~/.aws", "~/.config/gh", "~/.netrc"], SandboxPolicy.DenyRead(Home("ssh", "aws", "gh", "netrc")));
        Assert.Equal(["~/.aws", "~/.netrc"], SandboxPolicy.DenyRead(Home("aws", "netrc")));
        Assert.Empty(SandboxPolicy.DenyRead(Home()));
    }

    [Fact]
    public void Both_backends_deny_only_the_secret_paths_that_exist()
    {
        var home = Home("aws", "gh");
        var claude = SandboxLaunch.Create(_dir.Path, BackendCatalog.Claude, _dir.File("sandbox"), "job-2", home);
        var settings = JsonNode.Parse(File.ReadAllText(claude.ClaudeSettingsPath!))!;
        var codex = SandboxLaunch.Create(_dir.Path, BackendCatalog.Codex, _dir.File("sandbox"), "job-3", home).CodexArguments(false)[1];

        Assert.Equal(["~/.aws", "~/.config/gh"], settings["sandbox"]!["filesystem"]!["denyRead"]!.AsArray().Select(n => (string)n!));
        Assert.Contains("'~/.aws'='none'", codex);
        Assert.Contains("'~/.config/gh'='none'", codex);
        Assert.DoesNotContain("~/.ssh", codex);
        Assert.DoesNotContain("~/.netrc", codex);
    }

    [Theory]
    [InlineData("CLAUDE_CODE_USE_BEDROCK", "1", true)]
    [InlineData("CLAUDE_CODE_USE_VERTEX", "true", true)]
    [InlineData("CLAUDE_CODE_USE_BEDROCK", "0", false)]
    [InlineData("OTHER", "1", false)]
    public void A_claude_that_needs_cloud_credentials_downgrades_to_full_access(string variable, string value, bool downgraded)
    {
        var reason = SandboxCapability.Default(BackendCatalog.Claude, null, null, name => name == variable ? value : null);

        if (downgraded) { Assert.Equal(SafetyDowngrade.Unavailable, reason); }
        else { Assert.Equal(OperatingSystem.IsLinux() ? null : SafetyDowngrade.Unavailable, reason); }
    }
}
