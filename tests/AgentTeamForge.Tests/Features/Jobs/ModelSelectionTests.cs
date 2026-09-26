using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class ModelSelectionTests
{
    static IReadOnlyCollection<string> AllModels(string _) => ["gpt-6-luna", "gpt-6-sol", "gpt-6-astra"];

    [Theory]
    [InlineData("cheapest", "gpt-6-luna", "high")]
    [InlineData("low", "gpt-6-luna", "xhigh")]
    [InlineData("medium", "gpt-6-luna", "max")]
    [InlineData("high", "gpt-6-sol", "high")]
    [InlineData("xhigh", "gpt-6-astra", "low")]
    [InlineData("max", "gpt-6-astra", "medium")]
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
        Assert.Equal(("gpt-6-sol", "medium"), ModelSelection.Resolve("pi", "medium-fast", "low", AllModels));
        Assert.Equal(("vendor/custom", "high"), ModelSelection.Resolve("pi", "vendor/custom", "high", _ => []));
        Assert.Equal(("vendor/custom", "high"), ModelSelection.Resolve("codex", "vendor/custom", "high", _ => []));
    }

    [Fact]
    public void Retired_tier_and_unavailable_model_fail_with_guidance()
    {
        Assert.Contains("use 'high'", Assert.Throws<ArgumentException>(() => ModelSelection.Resolve("pi", "high-fast", null, AllModels)).Message);
        Assert.Contains("npm install -g @openai/codex@latest", Assert.Throws<ArgumentException>(() =>
            ModelSelection.Resolve("codex", "max", null, _ => ["gpt-6-luna"])).Message);
        Assert.Contains("npm install -g @earendil-works/pi-coding-agent@latest", Assert.Throws<ArgumentException>(() =>
            ModelSelection.Resolve("pi", "high", null, _ => ["gpt-6-luna"])).Message);
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
        Assert.Equal(("gpt-6-sol", "high"), (result.Job!.Model, result.Job.Effort));
        Assert.Equal(("gpt-6-sol", "high"), (fixture.Get().Execute(result.Job.JobId).Job!.Model,
            fixture.Get().Execute(result.Job.JobId).Job!.Effort));
        Assert.Equal(("gpt-6-sol", "high"), (fixture.List().Execute(new ListJobsRequest()).Page!.Jobs.Single().Model,
            fixture.List().Execute(new ListJobsRequest()).Page!.Jobs.Single().Effort));
    }
}
