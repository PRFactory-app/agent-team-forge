using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Observed per-run milestones; silence is not proof of success or failure.</summary>
public sealed record StartupProgress(string Phase, string StartedAt, string? ReadyAt, string? SubmittedAt,
    string? AcknowledgedAt, long ElapsedSeconds, string? Hint)
{
    public bool NoMarkerSinceLaunch { get; init; }

    internal static StartupProgress? Read(JobStore store, string jobId, string status, string? backend, string? reason)
    {
        if (status != JobStatus.Running) { return null; }
        IReadOnlyList<RunRecord> runs;
        try { runs = store.GetRuns(jobId); }
        catch (StorageException) { return null; } // Diagnostics must not hide the job itself.
        var run = runs.Count == 0 ? null : runs[^1];
        if (run?.StartedAt is not { } started) { return null; }
        var phase = run.AcknowledgedAt is not null || run.Acked ? "acknowledged"
            : run.SubmittedAt is not null ? "submitted" : run.ReadyAt is not null ? "ready" : "starting";
        if (phase == "acknowledged") { return null; }
        var end = run.AcknowledgedAt ?? run.FinishedAt;
        var elapsed = Math.Max(0, (long)((end is null ? DateTimeOffset.UtcNow : DateTimeOffset.Parse(end)) - DateTimeOffset.Parse(started)).TotalSeconds);
        var command = backend is "claude" or "codex" or "pi" ? backend : null;
        var noMarker = command is not null && elapsed >= 45 && run.AcknowledgedAt is null && !run.Acked;
        var hint = noMarker
            ? $"No state marker since launch {elapsed}s ago. The agent may be waiting for setup, login or workspace trust; inspect its terminal. Missing evidence does not confirm delivery or failure."
            : reason switch
            {
                "agent_login_required" => $"Login may be required; run `{command}` once in a terminal to log in.",
                "agent_first_run_required" => $"First-run setup may be required; run `{command}` once in a terminal to finish setup.",
                "agent_workspace_trust_required" => $"Workspace trust may require attention; run `{command}` in this workspace.",
                _ => null,
            };
        return new(phase, started, run.ReadyAt, run.SubmittedAt, run.AcknowledgedAt, elapsed, hint)
        {
            NoMarkerSinceLaunch = noMarker,
        };
    }
}
