namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>Explicit refusal when a headless-only CLI is selected in terminal mode.</summary>
public sealed class HeadlessOnlyBackend(string name) : IJobBackend
{
    public IBackendRun Start(BackendRequest request) =>
        throw new BackendNotStartedException($"{name} is headless-only in ATF; choose --mode headless to run it");
}
