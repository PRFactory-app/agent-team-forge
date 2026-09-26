namespace AgentTeamForge.DAL.Sqlite;

/// <summary>
/// Named durable boundaries. Production composition leaves the callback unset;
/// only an explicit test profile installs one (abrupt kill or injected failure).
/// </summary>
public sealed class DurabilityCheckpoints(Action<string>? onCheckpoint)
{
    public const string AcceptBeforeCommit = "accept.before-commit";
    public const string AcceptAfterCommit = "accept.after-commit";
    public const string AttemptAfterCommit = "attempt.after-commit";
    public const string CompleteBeforeCommit = "complete.before-commit";

    public static readonly DurabilityCheckpoints None = new(null);

    public void Hit(string name) => onCheckpoint?.Invoke(name);
}
