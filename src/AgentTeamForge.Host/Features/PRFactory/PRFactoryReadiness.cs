using AgentTeamForge.Business.Features.Jobs;

namespace AgentTeamForge.Host.Features.PRFactory;

public sealed record BackendReadiness(string Backend, bool Installed, bool SignedIn, bool ModelAvailable,
    bool ResumeSupported, string LaunchMode, DateTimeOffset ObservedAt, string? Blocker);

/// <summary>Explicit probe results for registration/heartbeat. Unknown authentication is never advertised as ready.</summary>
public static class PRFactoryReadiness
{
    public static IReadOnlyList<BackendReadiness> Report(IEnumerable<string> configuredBackends,
        string launchMode, IReadOnlyDictionary<string, bool?> signedIn,
        IReadOnlyDictionary<string, bool?> modelAvailable,
        IReadOnlySet<string> resumeSupported, DateTimeOffset observedAt)
    {
        var installed = BackendAvailability.Read(configuredBackends);
        return [.. installed.Select(pair =>
        {
            var auth = signedIn.GetValueOrDefault(pair.Key);
            var model = modelAvailable.GetValueOrDefault(pair.Key);
            var resume = resumeSupported.Contains(pair.Key);
            var blocker = !pair.Value ? "not_installed"
                : auth is not true ? "sign_in_unverified"
                : model is not true ? "model_unverified"
                : !resume ? "resume_unverified" : null;
            return new BackendReadiness(pair.Key, pair.Value, auth is true, model is true,
                resume, launchMode, observedAt, blocker);
        })];
    }
}
