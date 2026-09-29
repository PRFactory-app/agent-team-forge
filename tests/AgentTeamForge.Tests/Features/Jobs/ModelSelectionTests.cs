using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class ModelSelectionTests
{
    static IReadOnlyCollection<string> AllModels(string _) => ["gpt-6-luna", "gpt-6.1-sol", "gpt-6-astra"];

    [Fact]
    public void Console_choices_match_resolved_tiers_and_backend_effort_rules()
    {
        foreach (var backend in new[] { "codex", "pi" })
        {
            var options = ModelSelection.ConsoleOptions[backend];
            Assert.Empty(options.Efforts);
            foreach (var tier in options.Models)
            {
                Assert.True(ModelSelection.ValidConsoleSelection(backend, tier, null));
                Assert.NotNull(ModelSelection.Resolve(backend, tier, null, AllModels).Model);
            }
        }
        Assert.DoesNotContain("high-fast", ModelSelection.ConsoleOptions["pi"].Models);
        Assert.DoesNotContain("medium-fast", ModelSelection.ConsoleOptions["codex"].Models);
        Assert.False(ModelSelection.ValidConsoleSelection("codex", "high", "low"));
        Assert.True(ModelSelection.ValidConsoleSelection("claude", "opus", "medium"));
    }

    [Theory]
    [InlineData("cheapest", "gpt-6-luna", "low")]
    [InlineData("low", "gpt-6-luna", "medium")]
    [InlineData("medium", "gpt-6-luna", "high")]
    [InlineData("high", "gpt-6.1-sol", "high")]
    [InlineData("xhigh", "gpt-6-astra", "xhigh")]
    [InlineData("max", "gpt-6-astra", "max")]
    public void Shared_tiers_resolve_to_exact_backend_arguments(string tier, string model, string effort)
    {
        foreach (var backend in new[] { "codex", "pi" })
        {
            var selected = ModelSelection.Resolve(backend, tier, "ultra", AllModels);
            Assert.Equal((model, effort), selected);
            var request = new BackendRequest("job", "corr", "task", $"model={selected.Model};effort={selected.Effort}");
            var args = backend == "codex" ? CodexExecBackend.BuildArguments(request) : PiBackend.BuildArguments(request);
            if (backend == "codex")
            {
                Assert.Equal(["-m", model], args.SkipWhile(arg => arg != "-m").Take(2));
                Assert.Equal(["-c", $"model_reasoning_effort=\"{effort}\""], args.SkipWhile(arg => arg != "-c").Take(2));
            }
            else
            {
                Assert.Equal(["--model", "openai-codex/" + model], args.SkipWhile(arg => arg != "--model").Take(2));
                Assert.Equal(["--thinking", effort], args.SkipWhile(arg => arg != "--thinking").Take(2));
            }
        }
    }

    [Fact]
    public void Pi_medium_fast_and_raw_slug()
    {
        Assert.Equal(("gpt-6.1-sol", "medium"), ModelSelection.Resolve("pi", "medium-fast", "low", AllModels));
        Assert.Equal(("vendor/custom", "high"), ModelSelection.Resolve("pi", "vendor/custom", "high", _ => []));
        Assert.Equal(("vendor/custom", "high"), ModelSelection.Resolve("codex", "vendor/custom", "high", _ => []));
    }

    [Fact]
    public void Retired_tier_and_unavailable_model_fail_with_guidance()
    {
        Assert.Contains("use 'high'", Assert.Throws<ArgumentException>(() => ModelSelection.Resolve("pi", "high-fast", null, AllModels)).Message);
        Assert.Contains("not available", Assert.Throws<ArgumentException>(() =>
            ModelSelection.Resolve("codex", "medium-fast", null, AllModels)).Message);
        Assert.Contains("npm install -g @openai/codex@latest", Assert.Throws<ArgumentException>(() =>
            ModelSelection.Resolve("codex", "max", null, _ => ["gpt-6-luna"])).Message);
        Assert.Contains("npm install -g @earendil-works/pi-coding-agent@latest", Assert.Throws<ArgumentException>(() =>
            ModelSelection.Resolve("pi", "high", null, _ => ["gpt-6-luna"])).Message);
    }

    [Theory]
    [InlineData("codex", "max", "gpt-6-luna")]
    [InlineData("pi", "medium-fast", "gpt-6-luna")]
    public void Known_missing_tier_is_rejected_before_a_job_exists(string backend, string tier, string available)
    {
        using var fixture = new JobFixture();
        var accept = new AcceptJob(fixture.Store, JobFixture.Operator, fixture.Limits, fixture.TestProfile,
            fixture.Admission, [backend], _ => [available]);

        var refused = accept.Execute(new SubmitJobRequest("missing", "task", null, false)
        {
            Backend = backend,
            Model = tier,
        });

        Assert.Contains("npm install -g", refused.Error);
        Assert.Empty(fixture.List().Execute(new ListJobsRequest()).Page!.Jobs);
    }

    [Fact]
    public void Unknown_catalog_allows_tier_and_keeps_its_effort()
    {
        using var fixture = new JobFixture();
        var accept = new AcceptJob(fixture.Store, JobFixture.Operator, fixture.Limits, fixture.TestProfile,
            fixture.Admission, ["codex"], _ => []);

        var accepted = accept.Execute(new SubmitJobRequest("unknown", "task", null, false)
        {
            Backend = "codex",
            Model = "max",
            Effort = "low",
        });

        Assert.Equal("accepted", accepted.Outcome);
        Assert.Equal(("gpt-6-astra", "max"), (accepted.Job!.Model, accepted.Job.Effort));
    }

    [Fact]
    public void Discovery_parses_live_shapes_caches_results_and_times_out()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var dir = new TempStateDir();
        var script = dir.File("models");
        var calls = dir.File("calls");
        File.WriteAllText(script, $"#!/bin/sh\nprintf x >> '{calls}'\nif [ \"$1\" = debug ]; then\n  echo '{{\"models\":[{{\"slug\":\"gpt-6-sol\",\"supported_in_api\":true,\"visibility\":\"list\"}},{{\"slug\":\"hidden\",\"supported_in_api\":true,\"visibility\":\"hide\"}}]}}'\nelse\n  printf 'provider model context\\nopenai-codex gpt-6-luna 1M\\n'\nfi\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var discovery = new BackendModelDiscovery(TimeSpan.FromSeconds(2), _ => script);

        Assert.Equal(["gpt-6-sol"], discovery.GetModels("codex"));
        Assert.Equal(["gpt-6-luna"], discovery.GetModels("pi"));
        Assert.Equal(["gpt-6-sol"], discovery.GetModels("codex"));
        Assert.Equal("xx", File.ReadAllText(calls));

        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var completed = dir.File("completed");
        File.WriteAllText(script, $"#!/bin/sh\nprintf x >> '{calls}'\nsleep 5\nprintf done >> '{completed}'\n");
        var hung = new BackendModelDiscovery(TimeSpan.FromMilliseconds(100), _ => script);
        Assert.Empty(hung.GetModels("codex"));
        Assert.Empty(hung.GetModels("codex"));
        Assert.Equal("xxx", File.ReadAllText(calls));

        // An unknown catalog is not cached for the daemon lifetime.
        var retried = new BackendModelDiscovery(TimeSpan.FromMilliseconds(100), _ => script, unknownTtl: TimeSpan.Zero);
        Assert.Empty(retried.GetModels("codex"));
        Assert.Empty(retried.GetModels("codex"));
        Assert.Equal("xxxxx", File.ReadAllText(calls));
        Assert.False(File.Exists(completed));
    }

    [Theory]
    [InlineData(null, "opus")]
    [InlineData("fast", "haiku")]
    [InlineData("balanced", "sonnet")]
    [InlineData("powerful", "opus")]
    [InlineData("sonnet", "sonnet")]
    [InlineData("fable", "fable")]
    public void Claude_aliases_and_effort(string? input, string model)
    {
        Assert.Equal((model, "xhigh"), ModelSelection.Resolve("claude", input, "xhigh"));
    }

    [Fact]
    public void Default_sol_tiers_fall_back_to_listed_candidate_but_overrides_stay_strict()
    {
        static IReadOnlyCollection<string> OldOnly(string _) => ["gpt-6-sol"];
        Assert.Equal(("gpt-6-sol", "high"), ModelSelection.Resolve("codex", "high", null, OldOnly));
        Assert.Equal(("gpt-6-sol", "medium"), ModelSelection.Resolve("pi", "medium-fast", null, OldOnly));
        Assert.Equal(("gpt-6.1-sol", "high"), ModelSelection.Resolve("codex", "high", null, AllModels));
        Assert.Equal(("gpt-6-sol", "high"), ModelSelection.Resolve("codex", "high", null, _ => []));

        using var state = new TempStateDir();
        File.WriteAllText(Path.Combine(state.Path, "tier-map.json"),
            "[{\"backend\":\"codex\",\"tier\":\"high\",\"model\":\"gpt-6.1-sol\",\"effort\":\"high\"}]");
        var map = new TierMap(state.Path, OldOnly);
        Assert.Throws<ArgumentException>(() => ModelSelection.Resolve("codex", "high", null, OldOnly, map));
        Assert.Equal("gpt-6-sol", map.Settings().Single(row => row.Backend == "pi" && row.Tier == "medium-fast").Model);
    }

    [Fact]
    public void Saving_a_tier_compares_with_the_shown_default()
    {
        using var state = new TempStateDir();
        var map = new TierMap(state.Path, _ => []); // unknown catalog: the row shows gpt-6-sol
        TierSetting row() => map.Settings().Single(item => item.Backend == "codex" && item.Tier == "high");
        Assert.Equal("gpt-6-sol", row().Model);

        map.Change("codex", "high", "gpt-6-sol", "high");
        Assert.False(row().Custom);

        map.Change("codex", "high", "gpt-6.1-sol", "high");
        Assert.True(row().Custom);
        Assert.Equal("gpt-6.1-sol", row().Model);

        map.Change("codex", "high", "gpt-6.1-sol", "high");
        Assert.True(row().Custom);
        Assert.Equal("gpt-6.1-sol", row().Model);

        map.Change("codex", "high", "gpt-6-sol", "high");
        Assert.False(row().Custom);
        Assert.Equal("gpt-6-sol", row().Model);
    }

    [Fact]
    public void Acceptance_persists_concrete_model_and_effort_for_job_views()
    {
        using var fixture = new JobFixture();
        var accept = new AcceptJob(fixture.Store, JobFixture.Operator, fixture.Limits, fixture.TestProfile,
            fixture.Admission, ["fake", "codex"], AllModels);
        var result = accept.Execute(new SubmitJobRequest("tier", "task", null, false)
        {
            Backend = "codex",
            Model = "high",
            Effort = "low",
        });

        Assert.Equal("accepted", result.Outcome);
        Assert.Equal(("gpt-6.1-sol", "high"), (result.Job!.Model, result.Job.Effort));
        Assert.Equal(("gpt-6.1-sol", "high"), (fixture.Get().Execute(result.Job.JobId).Job!.Model,
            fixture.Get().Execute(result.Job.JobId).Job!.Effort));
        Assert.Equal(("gpt-6.1-sol", "high"), (fixture.List().Execute(new ListJobsRequest()).Page!.Jobs.Single().Model,
            fixture.List().Execute(new ListJobsRequest()).Page!.Jobs.Single().Effort));
    }

    [Fact]
    public async Task Follow_up_inherits_resolved_selection_and_resolves_overrides()
    {
        using var fixture = new JobFixture();
        using var state = new TempStateDir();
        var tierMap = new TierMap(state.Path, AllModels);
        var accept = new AcceptJob(fixture.Store, JobFixture.Operator, fixture.Limits, fixture.TestProfile,
            fixture.Admission, ["fake", "codex"], AllModels, tierMap);
        var parent = accept.Execute(new SubmitJobRequest("parent", "task", null, false)
        { Backend = "codex", Model = "cheapest", TargetAgent = "codex-named" }).Job!;
        var backend = new ScriptedBackend(r =>
        [
            new BackendEvidence.Ack(r.Correlation),
            new BackendEvidence.Session(r.Correlation, "session-1"),
            new BackendEvidence.Result(r.Correlation, "done"),
            new BackendEvidence.EndOfOutput(),
        ]);
        using var dispatcher = new DispatchJob(fixture.Store, new BackendCatalog().Register("codex", () => backend), fixture.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        Assert.Equal(JobStatus.Completed, fixture.Store.GetJob(parent.JobId)!.Status);
        tierMap.Change("codex", "cheapest", "gpt-6.1-sol", "xhigh");

        var followUp = new FollowUpJob(fixture.Store, JobFixture.Operator, accept);
        var inherited = followUp.Execute(new FollowUpRequest(parent.JobId, "again", "f1")).Job!;
        var effortOnly = followUp.Execute(new FollowUpRequest(parent.JobId, "again", "f2") { Effort = "low" }).Job!;
        var tier = followUp.Execute(new FollowUpRequest(parent.JobId, "again", "f3") { Model = "max", Effort = "low" }).Job!;
        var unavailable = followUp.Execute(new FollowUpRequest(parent.JobId, "again", "f4") { Model = "gpt-7" });
        var changedTier = followUp.Execute(new FollowUpRequest(parent.JobId, "again", "f5") { Model = "cheapest" }).Job!;

        Assert.Equal(("gpt-6-luna", "low"), (inherited.Model, inherited.Effort));
        Assert.Equal("codex-named", fixture.Store.GetJob(inherited.JobId)!.TargetAgent);
        Assert.Equal(("gpt-6-luna", "low"), (effortOnly.Model, effortOnly.Effort));
        Assert.Equal(("gpt-6-astra", "max"), (tier.Model, tier.Effort));
        Assert.Equal(("gpt-6.1-sol", "xhigh"), (changedTier.Model, changedTier.Effort));
        Assert.Contains("not available", unavailable.Error);
    }
}
