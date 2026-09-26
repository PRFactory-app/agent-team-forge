using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Business.Features.Recovery;

/// <summary>
/// Runs before the daemon serves requests or dispatches. Unattempted intents
/// stay eligible for one dispatch; started attempts without committed
/// completion are quarantined. On Linux, leftover backend processes are killed
/// only when they carry an interrupted run's random marker in their environment.
/// </summary>
public sealed class RecoverOnStartup(JobStore store, Action? recoverOwnedTerminals = null)
{
    public IReadOnlyList<string> Execute()
    {
        recoverOwnedTerminals?.Invoke();
        // Retry cleanup if an earlier restart died during recovery.
        OrphanedBackendProcess.TerminateMarked(store.GetInterruptedRunCorrelations());

        return store.QuarantineUncertainAttempts();
    }
}
