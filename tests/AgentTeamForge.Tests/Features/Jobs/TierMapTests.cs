using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class TierMapTests
{
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
        Assert.Equal(("gpt-6-astra", "low"), (pi.Model, pi.Effort));
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
}
