using System.Text.Json;
using System.Text.Json.Serialization;
using AgentTeamForge.Business;
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
        ProfileFile? profile;
        try
        {
            profile = JsonSerializer.Deserialize(StateDirectory.ReadPrivateFile(state.ProfileFile), SetupJson.Default.ProfileFile);
        }
        catch (JsonException)
        {
            throw new StateDirectoryException("profile_invalid", $"invalid JSON in {state.ProfileFile}; run atf setup or atf doctor to inspect configuration");
        }
        if (profile is null)
        {
            throw new StateDirectoryException("profile_invalid", $"invalid JSON in {state.ProfileFile}; run atf setup or atf doctor to inspect configuration");
        }
        if (profile.Backend is not (FakeBackends or AgentBackends))
        {
            // Explicit selection only: there is no fallback from a real backend to fake.
            throw new StateDirectoryException("backend_unsupported");
        }

        if (!LimitsAreValid(profile.QueueLimit, profile.MaxFakeRuntimeSeconds)
            || profile.MaxConcurrentJobs is not (null or (>= 1 and <= MaxConcurrencyLimit))
            || profile.PruneOlderThanDays is < 1 or > 36500)
        {
            // Refused before the daemon binds or reports readiness.
            throw new StateDirectoryException("profile_invalid_limits");
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
