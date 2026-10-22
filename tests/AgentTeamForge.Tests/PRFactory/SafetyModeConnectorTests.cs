using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Host.Features.PRFactory;

namespace AgentTeamForge.Tests.PRFactory;

/// <summary>Epic 52 worker contract: sandbox-v1, the safetyMode of every repository and the enforced-mode report on acceptance.</summary>
public sealed class SafetyModeConnectorTests
{
    static PRFactoryWorkItem Item(string? mode) => new()
    {
        Id = Guid.NewGuid(), Type = "Implementation", RepositoryId = Guid.NewGuid(), LeaseToken = Guid.NewGuid(),
        AgentType = PRFactoryAgentType.Codex, Prompt = "Do it", SafetyMode = mode,
    };

    /// <summary>Ticks once with a client that has read the server's capabilities, as the daemon's registration does.</summary>
    static async Task Tick(ChainHarness h, Func<string, string?>? sandboxDowngrade = null)
    {
        var client = h.Server.Client();
        await client.SupportsMultiRepoAsync(CancellationToken.None);
        await h.Adapter(sandboxDowngrade: sandboxDowngrade, client: client).TickAsync(ChainHarness.Machine, CancellationToken.None);
    }

    static JobSafety JobMode(ChainHarness h) => JobSafety.Read(Assert.Single(h.Store.ListJobs("prfactory", "connector", 10)).Options);

    [Theory]
    [InlineData("sandboxed")]
    [InlineData(null)] // A server that lists sandbox-v1 and omits the field means Sandboxed.
    [InlineData("somethingNew")]
    public async Task Capable_server_runs_the_job_sandboxed_and_reports_it(string? mode)
    {
        using var h = new ChainHarness(Item(mode));
        h.Server.SandboxSupported = true;
        await Tick(h);

        Assert.Contains(h.Server.PollQueries, q => q.Contains("capabilities=sandbox-v1", StringComparison.Ordinal));
        Assert.Equal("sandbox-v1", Assert.Single(Assert.Single(h.Server.ClaimBodies).GetProperty("capabilities").EnumerateArray()).GetString());
        var accept = Assert.Single(h.Server.AcceptBodies);
        Assert.Equal("sandbox-v1", Assert.Single(accept.GetProperty("capabilities").EnumerateArray()).GetString());
        Assert.Equal("sandboxed", accept.GetProperty("effectiveSafetyMode").GetString());
        Assert.False(accept.TryGetProperty("safetyDowngradeReason", out _));
        Assert.True(JobMode(h).RequestedSandboxed);
    }

    [Fact]
    public async Task Legacy_server_without_the_field_keeps_full_access_and_sends_nothing_new()
    {
        using var h = new ChainHarness(Item(null));
        await Tick(h);

        Assert.DoesNotContain(h.Server.PollQueries, q => q.Contains("sandbox-v1", StringComparison.Ordinal));
        Assert.False(Assert.Single(h.Server.ClaimBodies).TryGetProperty("capabilities", out _));
        var accept = Assert.Single(h.Server.AcceptBodies);
        Assert.False(accept.TryGetProperty("effectiveSafetyMode", out _));
        Assert.False(accept.TryGetProperty("capabilities", out _));
        Assert.Equal(JobSafety.FullAccess, JobMode(h));
    }

    [Fact]
    public async Task Full_access_item_reports_full_access()
    {
        using var h = new ChainHarness(Item("fullAccess"));
        h.Server.SandboxSupported = true;
        await Tick(h);

        Assert.Equal("fullAccess", Assert.Single(h.Server.AcceptBodies).GetProperty("effectiveSafetyMode").GetString());
        Assert.Equal(JobSafety.FullAccess, JobMode(h));
    }

    [Fact]
    public async Task A_worker_that_cannot_enforce_reports_the_downgrade()
    {
        using var h = new ChainHarness(Item("sandboxed"));
        h.Server.SandboxSupported = true;
        await Tick(h, _ => SafetyDowngrade.Unavailable);

        var accept = Assert.Single(h.Server.AcceptBodies);
        Assert.Equal("fullAccess", accept.GetProperty("effectiveSafetyMode").GetString());
        Assert.Equal(SafetyDowngrade.Unavailable, accept.GetProperty("safetyDowngradeReason").GetString());
        Assert.Contains(h.Logs, line => line.Contains("runs with full access here", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sandbox_refusal_skips_only_that_item()
    {
        using var h = new ChainHarness(Item("sandboxed"));
        h.Server.SandboxSupported = true;
        h.Server.RefuseClaimForSandbox = true;
        await Tick(h);

        Assert.Contains(h.Logs, line => line.Contains("sandbox_v1_required; skipping this item", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Logs, line => line.Contains("stopping claims", StringComparison.Ordinal));
        Assert.Empty(h.Server.AcceptBodies);
        // The item stays offered; the next tick claims it.
        await Tick(h);
        Assert.Single(h.Server.AcceptBodies);
    }

    [Fact]
    public async Task Acceptance_reads_carry_the_capability_of_the_claim()
    {
        using var h = new ChainHarness(Item("sandboxed"));
        h.Server.SandboxSupported = true;
        await Tick(h);
        await Tick(h);

        Assert.NotEmpty(h.Server.AcceptanceQueries);
        Assert.All(h.Server.AcceptanceQueries, q => Assert.Contains("capabilities=sandbox-v1", q, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("fullAccess", "fullAccess", false, "fullAccess")]
    [InlineData("fullAccess", "sandboxed", false, "sandboxed")] // Any Sandboxed repository makes the whole job Sandboxed.
    [InlineData("sandboxed", "fullAccess", false, "sandboxed")]
    [InlineData("fullAccess", null, true, "sandboxed")] // A missing value under the contract means Sandboxed.
    [InlineData("fullAccess", null, false, "fullAccess")] // A claim saved before the contract keeps today's behaviour.
    public void Multi_repository_mode_is_the_strictest_of_all_repositories(string primary, string? secondary, bool contract, string expected)
    {
        var item = new PRFactoryWorkItem
        {
            SafetyMode = primary,
            AtfClaimedWithSandboxV1 = contract,
            ContextJson = "{\"repositories\":{\"secondary\":[{\"id\":\"" + Guid.NewGuid() + "\""
                + (secondary is null ? "" : ",\"safetyMode\":\"" + secondary + "\"") + "}]}}",
        };

        Assert.Equal(expected, PRFactoryWorkItems.RequestedSafetyMode(item));
    }
}
