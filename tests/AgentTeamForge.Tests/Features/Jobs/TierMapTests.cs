using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class TierMapTests
{
    [Fact]
    public void Built_in_codex_tier_effort_increases_with_tier()
    {
        string[] order = [.. TierMap.Efforts("codex")];
        var tiers = ModelSelection.TierNames("codex");
        var efforts = tiers.Select(tier => ModelSelection.DefaultTier("codex", tier).Effort).ToArray();
        Assert.Equal(efforts.OrderBy(effort => Array.IndexOf(order, effort)), efforts);
        Assert.NotEqual("max", ModelSelection.DefaultTier("codex", "medium").Effort);
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
        Assert.Equal(("gpt-6-astra", "xhigh"), (pi.Model, pi.Effort));
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
        Assert.Throws<ArgumentException>(() => map.Change("pi", "high-fast", "gpt-6-sol", "low"));
        Assert.Throws<ArgumentException>(() => map.Change("codex", "high", "gpt-6-sol", "off"));
        map.Change("codex", "high", "gpt-6-sol", "ultra");
        map.Change("codex", "high", "gpt-6-sol", "low");
        Assert.Throws<ArgumentException>(() => map.Change("codex", "high", "bad;slug", "low"));
        Assert.Contains("npm install -g", Assert.Throws<ArgumentException>(() =>
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
