using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class TierMapTests
{
    [Fact]
    public void Built_in_codex_tier_effort_is_non_decreasing_within_each_model()
    {
        string[] order = [.. ModelSelection.Efforts("codex")];
        var defaults = ModelSelection.TierNames("codex").Select(tier => ModelSelection.DefaultTier("codex", tier));
        foreach (var group in defaults.GroupBy(item => item.Model))
        {
            var efforts = group.Select(item => item.Effort).ToArray();
            Assert.Equal(efforts.OrderBy(effort => Array.IndexOf(order, effort)), efforts);
        }

        Assert.NotEqual("max", ModelSelection.DefaultTier("codex", "medium").Effort);
    }

    [Fact]
    public void Fallback_row_shows_what_resolve_launches_and_saving_it_stays_built_in()
    {
        using var state = new TempStateDir();
        static IReadOnlyCollection<string> WithoutTerra(string _) => ["gpt-6-luna", "gpt-6.1-sol"];
        var map = new TierMap(state.Path, WithoutTerra);
        TierSetting row(string tier) => map.Settings().Single(item => item.Backend == "codex" && item.Tier == tier);

        foreach (var tier in new[] { "low", "medium" })
        {
            var launched = ModelSelection.Resolve("codex", tier, null, WithoutTerra, map);
            Assert.Equal(launched, (row(tier).Model, row(tier).Effort));
        }
        Assert.Equal(("gpt-6.1-sol", "low"), (row("medium").Model, row("medium").Effort));

        // Saving the displayed pair matches the active built-in default, so no override is stored.
        map.Change("codex", "medium", row("medium").Model, row("medium").Effort);
        Assert.False(row("medium").Custom);
        Assert.Null(map.Override("codex", "medium"));
        Assert.Equal(("gpt-6.1-sol", "low"), ModelSelection.Resolve("codex", "medium", null, WithoutTerra, map));
    }

    [Fact]
    public void Save_is_private_durable_and_changes_admission_only_for_selected_backend()
    {
        using var state = new TempStateDir();
        var map = new TierMap(state.Path, _ => ["gpt-6-sol", "gpt-6-astra"]);
        map.Change("codex", "xhigh", "gpt-6-sol", "xhigh");
        var file = state.File("tier-map.json");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.Single(new TierMap(state.Path, _ => []).Settings(), row => row.Custom);
        using var fixture = new JobFixture();
        var accept = new AcceptJob(fixture.Store, JobFixture.Operator, fixture.Limits, fixture.TestProfile,
            fixture.Admission, ["codex", "pi"], _ => [], map);
        var codex = accept.Execute(new SubmitJobRequest("codex-tier", "task", null, false) { Backend = "codex", Model = "xhigh" }).Job!;
        var pi = accept.Execute(new SubmitJobRequest("pi-tier", "task", null, false) { Backend = "pi", Model = "xhigh" }).Job!;
        Assert.Equal(("gpt-6-sol", "xhigh"), (codex.Model, codex.Effort));
        Assert.Equal(("gpt-6-sol", "xhigh"), ModelSelection.Resolve("codex", "XHIGH", null, _ => [], map));
        Assert.Equal(("gpt-6.2-sol", "high"), (pi.Model, pi.Effort));
        map.Change("codex", "xhigh", null, null);
        Assert.DoesNotContain(new TierMap(state.Path, _ => []).Settings(), row => row.Custom);
        Assert.Equal(("gpt-6-sol", "xhigh"), (fixture.Get().Execute(codex.JobId).Job!.Model,
            fixture.Get().Execute(codex.JobId).Job!.Effort));
    }

    [Fact]
    public void Invalid_names_efforts_slugs_and_missing_catalog_models_do_not_change_file()
    {
        using var state = new TempStateDir();
        var map = new TierMap(state.Path, _ => ["gpt-6-sol"]);
        map.Change("codex", "high", "gpt-6-sol", "low");
        var before = File.ReadAllText(state.File("tier-map.json"));
        Assert.Throws<TierSettingException>(() => map.Change("pi", "high-fast", "gpt-6-sol", "low"));
        Assert.Throws<TierSettingException>(() => map.Change("codex", "high", "gpt-6-sol", "off"));
        map.Change("codex", "high", "gpt-6-sol", "ultra");
        map.Change("codex", "high", "gpt-6-sol", "low");
        Assert.Throws<TierSettingException>(() => map.Change("codex", "high", "bad;slug", "low"));
        Assert.Contains("npm install -g", Assert.Throws<TierSettingException>(() =>
            map.Change("codex", "high", "gpt-6-astra", "low")).Message);
        Assert.Equal(before, File.ReadAllText(state.File("tier-map.json")));
        map.Change(null, null, null, null, resetAll: true);
        Assert.DoesNotContain(map.Settings(), row => row.Custom);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""[{"backend":"codex","tier":"high","model":"bad;slug","effort":"low"}]""")]
    [InlineData("""[{}]""")]
    public void Corrupt_file_falls_back_to_defaults_and_is_replaced_on_save(string content)
    {
        using var state = new TempStateDir();
        File.WriteAllText(state.File("tier-map.json"), content);
        var logged = new List<string>();
        var map = new TierMap(state.Path, _ => [], logged.Add);
        Assert.Single(logged);
        Assert.DoesNotContain(map.Settings(), row => row.Custom);
        map.Change("codex", "high", "gpt-6-sol", "low");
        Assert.Single(new TierMap(state.Path, _ => []).Settings(), row => row.Custom);
    }
}
