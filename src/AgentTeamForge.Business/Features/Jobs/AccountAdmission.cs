using System.Globalization;
using System.Text.RegularExpressions;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Business.Features.Jobs;

public sealed record AccountLimitSignal(string Backend, string AccountKey, string Reason,
    DateTimeOffset ObservedAt, DateTimeOffset? ResetsAt);

/// <summary>Inspects backend-owned error evidence, never prompt text or ordinary assistant prose.</summary>
public static partial class AccountLimitDetector
{
    [GeneratedRegex(@"(?i)\b(?:you(?:'ve| have) hit your (?:usage )?limit|usage limit (?:reached|exceeded)|rate limit(?:ed)? (?:reached|exceeded)|out of usage)\b")]
    private static partial Regex ClaudeLimit();
    [GeneratedRegex(@"(?i)\b(?:usage limit (?:reached|exceeded)|rate limit(?:ed)? (?:reached|exceeded)|too many requests|insufficient quota)\b")]
    private static partial Regex CodexLimit();
    [GeneratedRegex(@"(?i)\b(?:usage limit (?:reached|exceeded)|rate limit(?:ed)? (?:reached|exceeded)|too many requests|insufficient quota)\b")]
    private static partial Regex PiLimit();
    [GeneratedRegex(@"(?i)\b(?:resets? (?:at|on)\s+|reset_at[=:]\s*)(?<when>\d{4}-\d\d-\d\d\s*[T ]\s*\d\d:\d\d(?::\d\d)?(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)?)")]
    private static partial Regex ResetTime();

    public static AccountLimitSignal? Inspect(string backend, string accountKey, string? errorCode,
        string? errorText, DateTimeOffset observedAt, DateTimeOffset? reportedReset = null)
    {
        if (string.IsNullOrWhiteSpace(backend) || string.IsNullOrWhiteSpace(accountKey)) { return null; }
        // The caller must supply a CLI exit/API-error code. Claude's transcript parser already
        // classifies API-error records as agent_api_error; normal transcript prose is excluded.
        if (errorCode is not ("agent_api_error" or "agent_rate_limited" or "rate_limit_error" or "rate_limit_exceeded"
            or "insufficient_quota" or "usage_limit_reached" or "cli_nonzero_exit")) { return null; }
        var pattern = backend switch
        {
            "claude" or "claude-code" => ClaudeLimit(),
            "codex" => CodexLimit(),
            "pi" => PiLimit(),
            _ => null
        };
        if (pattern is null || !(pattern.IsMatch(errorText ?? "")
            || errorCode is "agent_rate_limited" or "rate_limit_error" or "rate_limit_exceeded" or "insufficient_quota" or "usage_limit_reached"))
        {
            return null;
        }
        var match = ResetTime().Match(errorText ?? "");
        var reset = reportedReset;
        if (reset is null && match.Success && DateTimeOffset.TryParse(match.Groups["when"].Value,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            reset = parsed;
        }
        return new(backend, accountKey, "account_usage_limit", observedAt, reset);
    }
}

/// <summary>Core admission and parking policy; the caller stops/fences the active turn before Park.</summary>
public sealed class AccountAdmission(AccountWindowStore windows)
{
    public bool CanStart(string backend, string accountKey, DateTimeOffset now) =>
        !windows.IsBlocked(backend, accountKey, now);

    public bool ParkIfLimited(string jobId, string backend, string accountKey, string? sessionId,
        string? errorCode, string? errorText, DateTimeOffset observedAt, DateTimeOffset? reportedReset = null)
    {
        var signal = AccountLimitDetector.Inspect(backend, accountKey, errorCode, errorText, observedAt, reportedReset);
        if (signal is null) { return false; }
        windows.Park(jobId, signal.Backend, signal.AccountKey, signal.Reason, sessionId, observedAt, signal.ResetsAt);
        return true;
    }

    /// <summary>Blocks new turns after a terminal limit failure, without scheduling this job for a same-session resume.</summary>
    public bool BlockIfLimited(string backend, string accountKey, string? errorCode, string? errorText,
        DateTimeOffset observedAt)
    {
        var signal = AccountLimitDetector.Inspect(backend, accountKey, errorCode, errorText, observedAt);
        if (signal is null) { return false; }
        windows.Block(signal.Backend, signal.AccountKey, signal.Reason, signal.ObservedAt, signal.ResetsAt);
        return true;
    }

    public IReadOnlyList<AccountPark> Due(DateTimeOffset now) => windows.Due(now);

    public AccountPark? Park(string jobId) => windows.GetPark(jobId);

    /// <summary>Maps dispatcher agent-error codes to the detector's backend-owned evidence codes.</summary>
    public static string? EvidenceCode(string backend, string code) => code switch
    {
        "agent_error" => "agent_api_error",
        _ when code == backend + "_error" => "cli_nonzero_exit",
        _ => code
    };

    public bool TryBeginResume(AccountPark park, DateTimeOffset now) =>
        windows.MarkResuming(park.JobId, now);

    public void ResumeRecorded(string jobId) => windows.MarkResumed(jobId);

    public void RetryResume(string jobId) => windows.RetryResume(jobId);

    public void PermitAccountRecovery(string backend, string accountKey, DateTimeOffset now) =>
        windows.PermitAccountRecovery(backend, accountKey, now);

    public string? ReserveClaim(int maxAcceptedTeams, DateTimeOffset now) =>
        windows.TryReserveTeam(maxAcceptedTeams, now, TimeSpan.FromMinutes(2));

    public void ReleaseClaim(string token) => windows.ReleaseTeamReservation(token);
}
