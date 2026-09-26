using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Business.Features.Recovery;

/// <summary>
/// Runs before the daemon serves requests or dispatches. Unattempted intents
/// stay eligible for one dispatch; started attempts without committed
/// completion are quarantined. A Linux child is terminated only when its
/// recorded run marker still matches the process at that PID.
/// </summary>
public sealed class RecoverOnStartup(JobStore store)
{
    public IReadOnlyList<string> Execute()
    {
        // Retry cleanup if an earlier restart died during recovery.
        foreach (var (pid, correlation) in store.GetOrphanedBackendProcesses())
        {
            OrphanedBackendProcess.TryTerminate(pid, correlation);
        }

        return store.QuarantineUncertainAttempts();
    }
}
