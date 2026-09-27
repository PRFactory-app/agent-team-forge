using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Agents.Terminals;

public sealed class InteractiveAgentPreflightTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void Missing_or_incomplete_onboarding_fails_before_login_check(string? value)
    {
        using var state = new TempStateDir();
        var env = Env(state.Path);
        if (value is not null) { File.WriteAllText(state.File(".claude.json"), "{\"hasCompletedOnboarding\":" + value + "}"); }
        env["ANTHROPIC_API_KEY"] = "dummy";

        Assert.Equal("agent_first_run_required", Check(env, state.Path)?.Reason);
    }

    [Theory]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN")]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("ANTHROPIC_AUTH_TOKEN")]
    [InlineData("CLAUDE_CODE_USE_BEDROCK")]
    [InlineData("CLAUDE_CODE_USE_VERTEX")]
    [InlineData("CLAUDE_CODE_USE_FOUNDRY")]
    [InlineData("CLAUDE_CODE_USE_ANTHROPIC_AWS")]
    [InlineData("ANTHROPIC_AWS_API_KEY")]
    [InlineData("ANTHROPIC_FOUNDRY_API_KEY")]
    [InlineData("ANTHROPIC_BASE_URL")]
    [InlineData("ANTHROPIC_CUSTOM_HEADERS")]
    [InlineData("CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR")]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR")]
    [InlineData("CLAUDE_CODE_GATEWAY_TOKEN_FILE_DESCRIPTOR")]
    public void Alternate_credential_sources_prevent_login_failure(string name)
    {
        using var state = new TempStateDir();
        Ready(state.Path);
        var env = Env(state.Path);
        env[name] = "1";

        Assert.Null(Check(env, state.Path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Platforms_with_uninspectable_credential_stores_do_not_infer_missing_login(int platform)
    {
        using var state = new TempStateDir();
        Ready(state.Path);
        Assert.Null(Check(Env(state.Path), state.Path, (InteractivePlatform)platform));
    }

    [Fact]
    public void Linux_proven_missing_login_fails_and_existing_credential_file_passes()
    {
        using var state = new TempStateDir();
        Ready(state.Path);
        var env = Env(state.Path);
        Assert.Equal("agent_login_required", Check(env, state.Path)?.Reason);
        File.WriteAllText(Path.Combine(state.Path, ".claude", ".credentials.json"), "{}");
        Assert.Null(Check(env, state.Path));
    }

    [Theory]
    [InlineData("primaryApiKey")]
    [InlineData("oauthAccount")]
    public void Account_state_in_global_config_makes_missing_login_unprovable(string property)
    {
        using var state = new TempStateDir();
        Ready(state.Path);
        File.WriteAllText(state.File(".claude.json"), "{\"hasCompletedOnboarding\":true,\"" + property + "\":{}}");
        Assert.Null(Check(Env(state.Path), state.Path));
    }

    [Fact]
    public void Settings_that_may_supply_an_api_key_helper_do_not_infer_missing_login()
    {
        using var state = new TempStateDir();
        Ready(state.Path);
        var settings = Path.Combine(state.Path, ".claude", "settings.json");
        File.WriteAllText(settings, "{\"apiKeyHelper\":\"get-key\"}");
        Assert.Null(Check(Env(state.Path), state.Path));
        File.Delete(settings);
        // A project setting can supply an env block too.
        File.WriteAllText(Path.Combine(state.Path, ".claude", "settings.local.json"), "{}");
        Assert.Null(Check(Env(state.Path), state.Path));
    }

    [Fact]
    public void Override_uses_its_own_global_state_and_resolves_relative_to_working_directory()
    {
        using var state = new TempStateDir();
        var cwd = Directory.CreateDirectory(state.File("work")).FullName;
        var config = Directory.CreateDirectory(Path.Combine(cwd, "custom")).FullName;
        File.WriteAllText(state.File(".claude.json"), "{\"hasCompletedOnboarding\":true}");
        var env = Env(state.Path);
        env["CLAUDE_CONFIG_DIR"] = "custom";
        Assert.Equal("agent_first_run_required", Check(env, cwd)?.Reason);
        File.WriteAllText(Path.Combine(config, ".claude.json"), "{\"hasCompletedOnboarding\":true}");
        Assert.Equal("agent_login_required", Check(env, cwd)?.Reason);
    }

    [Fact]
    public void Legacy_config_json_takes_precedence_over_missing_global_file()
    {
        using var state = new TempStateDir();
        Directory.CreateDirectory(state.File(".claude"));
        File.WriteAllText(Path.Combine(state.Path, ".claude", ".config.json"), "{\"hasCompletedOnboarding\":true,\"oauthAccount\":{}}");
        Assert.Null(Check(Env(state.Path), state.Path));
    }

    [Fact]
    public void Unreadable_shape_and_other_agents_keep_the_bounded_path()
    {
        using var state = new TempStateDir();
        File.WriteAllText(state.File(".claude.json"), "broken");
        var env = Env(state.Path);
        Assert.Null(Check(env, state.Path));
        Assert.Null(InteractiveAgentPreflight.Check(InteractiveAgentKind.Codex, name => env.GetValueOrDefault(name), state.Path, InteractivePlatform.Linux));
    }

    [Fact]
    public void Owners_linux_config_copy_has_no_false_positive()
    {
        var source = Environment.GetEnvironmentVariable("ATF_CLAUDE_PREFLIGHT_OWNER_HOME");
        if (source is null) { return; }
        using var state = new TempStateDir();
        Directory.CreateDirectory(state.File(".claude"));
        File.Copy(Path.Combine(source, ".claude.json"), state.File(".claude.json"));
        foreach (var name in new[] { ".credentials.json", "settings.json" })
        {
            var path = Path.Combine(source, ".claude", name);
            if (File.Exists(path)) { File.Copy(path, Path.Combine(state.Path, ".claude", name)); }
        }
        var env = Env(state.Path);
        Assert.Null(Check(env, state.Path));
    }

    static Dictionary<string, string?> Env(string home) => new() { ["HOME"] = home };

    static AgentStartupBlockedException? Check(Dictionary<string, string?> env, string cwd,
        InteractivePlatform platform = InteractivePlatform.Linux) =>
        InteractiveAgentPreflight.Check(InteractiveAgentKind.Claude, name => env.GetValueOrDefault(name), cwd, platform);

    static void Ready(string home)
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Path.Combine(home, ".claude.json"), "{\"hasCompletedOnboarding\":true}");
    }
}
