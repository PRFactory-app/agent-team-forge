using System.Text.Json;
using AgentTeamForge.Business.Features.Jobs;

namespace AgentTeamForge.Host.Features.PRFactory;

/// <summary>Epic 52: the safety mode a claimed item asks for, and the mode this worker reports it enforces.</summary>
public sealed partial class PRFactoryWorkItems
{
    /// <summary>
    /// The whole job runs Sandboxed when any repository is Sandboxed (one agent process cannot be split). An unknown value counts as
    /// Sandboxed; a missing one only when the claim carried <c>sandbox-v1</c>, so a server without the contract keeps today's behaviour.
    /// </summary>
    internal static string RequestedSafetyMode(PRFactoryWorkItem item)
    {
        List<string?> modes = [item.SafetyMode, .. SecondaryModes(item.ContextJson)];
        return modes.Any(mode => mode is null ? item.AtfClaimedWithSandboxV1
            : !string.Equals(mode, SafetyMode.FullAccess, StringComparison.OrdinalIgnoreCase))
            ? SafetyMode.Sandboxed : SafetyMode.FullAccess;
    }

    /// <summary>Each secondary repository's <c>safetyMode</c> (null when absent); none when the context has no repository set.</summary>
    static IEnumerable<string?> SecondaryModes(string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson)) { return []; }
        try
        {
            using var json = JsonDocument.Parse(contextJson);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("repositories", out var repositories)
                || repositories.ValueKind != JsonValueKind.Object || !repositories.TryGetProperty("secondary", out var secondary)
                || secondary.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            return [.. secondary.EnumerateArray().Select(entry => entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("safetyMode", out var mode) ? mode.ValueKind == JsonValueKind.String ? mode.GetString() : "unknown" : null)];
        }
        // A malformed set fails the item later (PRFactoryRepositorySet); for the mode it counts as unknown, so Sandboxed.
        catch (JsonException) { return ["unknown"]; }
    }

    /// <summary>
    /// The acceptance report: Sandboxed only when every agent of the team can be sandboxed on this daemon, otherwise full access with the
    /// first downgrade reason. A full-access item reports full access. Predicted before any job exists, with the probe the jobs use.
    /// </summary>
    internal PRFactorySafetyReport SafetyReport(PRFactoryWorkItem item)
    {
        if (RequestedSafetyMode(item) != SafetyMode.Sandboxed) { return new(SafetyMode.FullAccess, null); }
        IEnumerable<PRFactoryAgentType> agents = [item.AgentType, .. item.TeamPlan?.Members.Select(m => m.Backend ?? item.AgentType) ?? []];
        var reason = agents.Select(MapBackend).OfType<string>().Distinct(StringComparer.Ordinal)
            .Select(backend => (sandboxDowngrade ?? (b => SandboxCapability.Default(b, null, null)))(backend)).FirstOrDefault(r => r is not null);
        return reason is null ? new(SafetyMode.Sandboxed, null) : new(SafetyMode.FullAccess, reason);
    }
}
