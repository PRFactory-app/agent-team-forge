using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>
/// Bounds an interactive turn that ended on a background task. A task that never reports back (or a
/// missed notification) must not hold the turn and its queued follow-ups for the 24h cap.
/// </summary>
internal sealed class InteractiveBackgroundWait(TimeSpan limit)
{
    internal const string ExpiredReason = "background_wait_expired";

    DateTimeOffset? _quietSince;
    DateTimeOffset? _activity;

    /// <summary>How long an idle agent may wait on its background task (ATF_BACKGROUND_WAIT_MINUTES, default 45).</summary>
    internal static TimeSpan DefaultLimit =>
        int.TryParse(Environment.GetEnvironmentVariable("ATF_BACKGROUND_WAIT_MINUTES"), out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes) : TimeSpan.FromMinutes(45);

    /// <summary>
    /// True when the agent waited on a background task without transcript activity, and with an idle
    /// terminal, for longer than the limit. Any activity or a busy terminal restarts the clock.
    /// </summary>
    public bool Expired(bool waiting, bool idle, DateTimeOffset? activity, DateTimeOffset now)
    {
        if (!waiting || !idle || activity != _activity || _quietSince is null)
        {
            _activity = activity;
            _quietSince = waiting && idle ? now : null;
            return false;
        }
        return now - _quietSince >= limit;
    }

    // The agent ended its turn, so the failure carries turn_ended and a queued follow-up can run.
    public BackendEvidence.AgentError Error() => new(ExpiredReason,
        $"The agent ended its turn on a background task and was idle for {limit.TotalMinutes:0} min without a task notification.",
        TurnEnded: true);
}
