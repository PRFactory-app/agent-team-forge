using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

/// <summary>Effort is checked against each backend's catalog before a job exists.</summary>
public sealed class ModelEffortValidationTests
{
    const string CodexCatalog = """
        {"models":[
          {"slug":"gpt-6-luna","supported_in_api":true,"visibility":"list","supported_reasoning_levels":[{"effort":"low"},{"effort":"medium"},{"effort":"max"}]},
          {"slug":"gpt-6.1-sol","supported_in_api":true,"visibility":"list","supported_reasoning_levels":[{"effort":"low"},{"effort":"ultra"}]},
          {"slug":"gpt-6-astra","supported_in_api":true,"visibility":"list"},
          {"slug":"hidden","supported_in_api":true,"visibility":"hide","supported_reasoning_levels":[{"effort":"low"}]}
        ]}
        """;

    static IReadOnlyCollection<string> Codex(string backend) => backend == "codex" ? BackendModelDiscovery.ParseCodex(CodexCatalog) : [];

    [Fact]
    public void Codex_catalog_keeps_listed_models_and_their_reported_levels()
    {
        var catalog = Assert.IsType<DiscoveredModelCollection>(BackendModelDiscovery.ParseCodex(CodexCatalog));

        Assert.Equal(["gpt-6-luna", "gpt-6.1-sol", "gpt-6-astra"], catalog);
        Assert.Equal(["low", "medium", "max"], catalog.EffortsFor("gpt-6-luna"));
        Assert.Null(catalog.EffortsFor("gpt-6-astra"));
        Assert.Equal(ModelSelection.Efforts("codex"), ModelSelection.Efforts("codex", "gpt-6-astra", catalog));
    }

    [Theory]
    [InlineData("claude", null, "bogus-eff", "Supported: low, medium, high, xhigh, max")]
    [InlineData("claude", "fast", "ultra", "Supported: low, medium, high, xhigh, max")]
    [InlineData("codex", "gpt-6-luna", "ultra", "codex model 'gpt-6-luna'. Supported: low, medium, max")]
    [InlineData("codex", null, "bogus", "Supported: none, minimal, low, medium, high, xhigh, max, ultra")]
    [InlineData("pi", null, "ultra", "Supported: off, minimal, low, medium, high, xhigh, max")]
    [InlineData("droid", null, "bogus", "Unsupported effort 'bogus' for droid")]
    public void Unsupported_effort_is_rejected_with_the_supported_list(string backend, string? model, string effort, string message)
    {
        var error = Assert.Throws<ArgumentException>(() => ModelSelection.Resolve(backend, model, effort, Codex));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude", "opus", "max", "opus", "max")]
    [InlineData("codex", "gpt-6.1-sol", "ultra", "gpt-6.1-sol", "ultra")]
    [InlineData("codex", "gpt-6-astra", "xhigh", "gpt-6-astra", "xhigh")]
    [InlineData("codex", "cheapest", "ultra", "gpt-6-luna", "low")]
    [InlineData("droid", null, "ultra", null, "ultra")]
    public void Supported_effort_and_tier_effort_pass(string backend, string? model, string effort, string? resolvedModel, string resolvedEffort) =>
        Assert.Equal((resolvedModel, resolvedEffort), ModelSelection.Resolve(backend, model, effort, Codex));

    [Fact]
    public void Custom_slug_with_an_unknown_catalog_is_checked_against_the_backend_list()
    {
        Assert.Equal(("vendor/custom", "ultra"), ModelSelection.Resolve("codex", "vendor/custom", "ultra", _ => []));
        Assert.Equal(("vendor/custom", "off"), ModelSelection.Resolve("pi", "vendor/custom", "off", _ => []));
        Assert.Throws<ArgumentException>(() => ModelSelection.Resolve("codex", "vendor/custom", "bogus", _ => []));
    }

    [Fact]
    public void Console_options_error_messages_and_validation_share_one_claude_catalog()
    {
        var options = ModelSelection.ConsoleOptions["claude"];
        var error = Assert.Throws<ArgumentException>(() => ModelSelection.Resolve("claude", "nope", null)).Message;

        Assert.EndsWith("Supported: " + string.Join(", ", options.Models), error, StringComparison.Ordinal);
        Assert.Equal(ModelSelection.Efforts("claude"), options.Efforts);
        foreach (var model in options.Models)
        {
            Assert.NotNull(ModelSelection.Resolve("claude", model, null).Model);
        }
        Assert.Equal(("effort", "Unsupported effort 'bogus' for claude. Supported: low, medium, high, xhigh, max"),
            ModelSelection.ConsoleSelectionError("claude", "opus", "bogus"));
    }

    [Fact]
    public void Claude_job_with_a_bogus_effort_is_refused_before_it_exists()
    {
        using var fixture = new JobFixture();
        var accept = new AcceptJob(fixture.Store, JobFixture.Operator, fixture.Limits, fixture.TestProfile,
            fixture.Admission, ["claude"]);

        var refused = accept.Execute(new SubmitJobRequest("bogus-effort", "task", null, false) { Backend = "claude", Effort = "bogus-eff" });

        Assert.Contains("Unsupported effort 'bogus-eff' for claude", refused.Error, StringComparison.Ordinal);
        Assert.Empty(fixture.List().Execute(new ListJobsRequest()).Page!.Jobs);
    }

    [Fact]
    public void Tier_change_checks_effort_against_the_chosen_model()
    {
        using var state = new TempStateDir();
        var map = new TierMap(state.Path, Codex);

        var effort = Assert.Throws<TierSettingException>(() => map.Change("codex", "cheapest", "gpt-6-luna", "ultra"));
        Assert.Equal(("effort", "Invalid effort: Unsupported effort 'ultra' for codex model 'gpt-6-luna'. Supported: low, medium, max"),
            (effort.Field, effort.Detail));
        Assert.Equal("model", Assert.Throws<TierSettingException>(() => map.Change("codex", "cheapest", "gpt-6-gone", "low")).Field);
        Assert.Equal("tier", Assert.Throws<TierSettingException>(() => map.Change("codex", "medium-fast", "gpt-6-luna", "low")).Field);
        Assert.Equal("backend", Assert.Throws<TierSettingException>(() => map.Change("claude", "high", "opus", "low")).Field);
        Assert.False(File.Exists(state.File("tier-map.json")));

        map.Change("codex", "high", "gpt-6.1-sol", "ultra");
        Assert.Equal(("gpt-6.1-sol", "ultra"), map.Override("codex", "high"));
    }
}
