using System.Text.Json;
using System.Text.Json.Serialization;
using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Host.Hosting;

namespace AgentTeamForge.Host.Features.Setup;

/// <summary>profile.json: the bound operator identity and spike limit overrides.</summary>
public sealed record SpikeProfileFile
{
    public required string Principal { get; init; }
    public required string Team { get; init; }
    public required string Agent { get; init; }
    public required string Backend { get; init; }
    public bool TestProfile { get; init; }
    public int? QueueLimit { get; init; }
    public int? MaxFakeRuntimeSeconds { get; init; }

    public const int MaxQueueLimit = 1_000;

    public BoundPrincipal Bound => new(Principal, Team, Agent);

    public SpikeLimits Limits
    {
        get
        {
            var limits = new SpikeLimits();
            return limits with
            {
                QueueLimit = QueueLimit ?? limits.QueueLimit,
                MaxFakeRuntime = MaxFakeRuntimeSeconds is { } s ? TimeSpan.FromSeconds(s) : limits.MaxFakeRuntime,
            };
        }
    }

    public static SpikeProfileFile Load(StateDirectory state)
    {
        var profile = JsonSerializer.Deserialize(StateDirectory.ReadPrivateFile(state.ProfileFile), SetupJson.Default.SpikeProfileFile)
            ?? throw new StateDirectoryException("profile_invalid");
        if (profile.Backend != "fake")
        {
            // Explicit selection only: there is no fallback from a real backend to fake.
            throw new StateDirectoryException("backend_unsupported");
        }

        if (!LimitsAreValid(profile.QueueLimit, profile.MaxFakeRuntimeSeconds))
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
[JsonSerializable(typeof(SpikeProfileFile))]
public sealed partial class SetupJson : JsonSerializerContext;
