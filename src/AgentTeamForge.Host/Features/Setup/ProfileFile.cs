using System.Text.Json.Serialization;
using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Host.Hosting;

namespace AgentTeamForge.Host.Features.Setup;

/// <summary>
/// profile.json: the bound operator identity, backend mode and limit overrides.
/// Backend "agents" enables the real agent CLIs (plus fake); "fake" is fake only.
/// A test profile never runs real agents.
/// </summary>
public sealed record ProfileFile
{
    public required string Principal { get; init; }
    public required string Team { get; init; }
    public required string Agent { get; init; }
    public required string Backend { get; init; }
    public bool TestProfile { get; init; }
    public int? QueueLimit { get; init; }
    public int? MaxFakeRuntimeSeconds { get; init; }
    public int? MaxConcurrentJobs { get; init; }
    public bool AutoPrune { get; init; } = true;
    public int PruneOlderThanDays { get; init; } = 30;

    /// <summary>Launch mode of local jobs that name none (<c>sandboxed</c> or <c>fullAccess</c>); null is fullAccess.</summary>
    public string? SafetyMode { get; init; }

    /// <summary>Shell network access inside the sandbox of this machine; null or true keeps it on, false turns it off (per-machine opt-in).</summary>
    public bool? SandboxNetwork { get; init; }

    /// <summary>The domains sandboxed Claude reaches when the network is on; null is the default list.</summary>
    public IReadOnlyList<string>? SandboxAllowedDomains { get; init; }

    /// <summary>The package cache paths sandboxed Codex and Claude may write; null is the default list for this OS.</summary>
    public IReadOnlyList<string>? SandboxCachePaths { get; init; }

    [JsonIgnore]
    public SandboxSettings Sandbox => SandboxSettings.From(SandboxNetwork, SandboxAllowedDomains, SandboxCachePaths);

    public const int MaxQueueLimit = 1_000;
    public const int MaxConcurrencyLimit = 64;
    public const string FakeBackends = "fake";
    public const string AgentBackends = "agents";

    /// <summary>Default turn deadline for real agents; the fake default stays short.</summary>
    const int AgentRuntimeSeconds = 3_600;

    public bool RealAgents => Backend == AgentBackends && !TestProfile;

    public BoundPrincipal Bound => new(Principal, Team, Agent);

    public SpikeLimits Limits
    {
        get
        {
            var limits = new SpikeLimits();
            return limits with
            {
                QueueLimit = QueueLimit ?? limits.QueueLimit,
                MaxConcurrentJobs = MaxConcurrentJobs ?? limits.MaxConcurrentJobs,
                MaxFakeRuntime = MaxFakeRuntimeSeconds is { } s ? TimeSpan.FromSeconds(s)
                    : RealAgents ? TimeSpan.FromSeconds(AgentRuntimeSeconds) : limits.MaxFakeRuntime,
            };
        }
    }

    public static ProfileFile Load(StateDirectory state)
    {
        var profile = StateJson.Read(state.ProfileFile, "profile_invalid", SetupJson.Default.ProfileFile, "principal", "team", "agent", "backend");
        if (profile.Backend is not (FakeBackends or AgentBackends))
        {
            // Explicit selection only: there is no fallback from a real backend to fake.
            throw new StateDirectoryException("backend_unsupported", $"{state.ProfileFile} has backend \"{profile.Backend}\"; expected \"{FakeBackends}\" or \"{AgentBackends}\"");
        }

        if (profile.SafetyMode is not null && !Business.Features.Jobs.SafetyMode.Valid(profile.SafetyMode))
        {
            throw new StateDirectoryException("profile_invalid_safety_mode", $"{state.ProfileFile}: safety_mode must be \"sandboxed\" or \"fullAccess\"");
        }

        // Only lists the profile sets are checked: a default must never stop the daemon from starting. Null and NUL entries are errors here,
        // not exceptions, so a bad hand edit is a StateDirectoryException that keeps the last good settings at runtime.
        if (profile.SandboxAllowedDomains?.Count > SandboxConfiguration.MaxDomains || profile.SandboxCachePaths?.Count > SandboxConfiguration.MaxCachePaths)
        {
            throw new StateDirectoryException("profile_invalid_sandbox", $"{state.ProfileFile}: at most {SandboxConfiguration.MaxDomains} domains and {SandboxConfiguration.MaxCachePaths} cache paths");
        }
        if (new SandboxSettings(true, profile.SandboxAllowedDomains ?? [], profile.SandboxCachePaths ?? []).Errors(stateRoot: state.Path) is [_, ..] sandboxErrors)
        {
            throw new StateDirectoryException("profile_invalid_sandbox", $"{state.ProfileFile}: {string.Join("; ", sandboxErrors)}");
        }

        if (!LimitsAreValid(profile.QueueLimit, profile.MaxFakeRuntimeSeconds)
            || profile.MaxConcurrentJobs is not (null or (>= 1 and <= MaxConcurrencyLimit))
            || profile.PruneOlderThanDays is < 1 or > 36500)
        {
            // Refused before the daemon binds or reports readiness.
            throw new StateDirectoryException("profile_invalid_limits", $"{state.ProfileFile}: queue_limit must be 1-{MaxQueueLimit}, "
                + $"max_fake_runtime_seconds 1-{(int)DispatchJob.MaxAllowedRuntime.TotalSeconds}, max_concurrent_jobs 1-{MaxConcurrencyLimit}, prune_older_than_days 1-36500");
        }

        return profile;
    }

    /// <summary>Overrides must be positive and bounded; absent values use the defaults.</summary>
    public static bool LimitsAreValid(int? queueLimit, int? maxRuntimeSeconds) =>
        queueLimit is null or (>= 1 and <= MaxQueueLimit)
        && (maxRuntimeSeconds is null || (maxRuntimeSeconds >= 1 && maxRuntimeSeconds <= DispatchJob.MaxAllowedRuntime.TotalSeconds));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(ProfileFile))]
public sealed partial class SetupJson : JsonSerializerContext;
