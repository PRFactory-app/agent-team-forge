using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Business.Features.Recovery;

/// <summary>
/// Runs before the daemon serves requests or dispatches. Unattempted intents
/// stay eligible for one dispatch; started attempts without committed
/// completion are quarantined. No process is adopted or killed by PID.
/// </summary>
public sealed class RecoverOnStartup(JobStore store)
{
    public IReadOnlyList<string> Execute() => store.QuarantineUncertainAttempts();
}
