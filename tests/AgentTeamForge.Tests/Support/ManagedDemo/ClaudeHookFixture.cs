using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentTeamForge.Tests.Support.ManagedDemo;

/// <summary>
/// Test-only characterization copy of the reviewed legacy Claude hook parser and capability gate.
/// Provenance: branch <c>spike/m0-claude-isolation</c> at <c>8a5e385dd11ca4105c145c452e990c823eaf2654</c>
/// (approved in <c>docs/spikes/m0-safety-lanes-review.md</c>, in git history): legacy Claude hook records/log,
/// delivery reconciliation/tag, and the Claude capability gate. Logic is kept verbatim apart
/// from nesting and naming. Inbox dispatch and prompt-tag
/// correlation (<c>ClaudeCorrelation</c>) are deliberately not imported. This is not a product
/// adapter; D11 replaces it with production code under a reviewed contract.
/// </summary>
public static partial class ClaudeHookFixture
{
    public enum JobState
    {
        Accepted,
        Dispatching,
        BackendAcknowledged,
        Completed,
        Interrupted,
        Failed,
        NeedsReconciliation,
    }

    public enum TurnStatus
    {
        InProgress,
        Completed,
        Interrupted,
        Failed,
        EndedWithoutResult,
    }

    public sealed record ObservedTurn(string? JobId, string TurnKey, TurnStatus Status, string? ResultText, string? AttemptId = null, bool Classified = true);

    public sealed record Reconciliation(JobState State, string? TurnKey, string? ResultText, string Reason, bool DuplicateDelivery = false);

    /// <summary>A Claude prompt lifecycle from hooks. Tags are what the prompt text claims, not proof of origin.</summary>
    public sealed record ClaudeTurn(string PromptId, IReadOnlyList<string> Tags, string? PromptSha256, double Timestamp, TurnStatus Status, string? Result);

    public sealed record ClaudeSessionView(
        IReadOnlyList<ClaudeTurn> Turns,
        bool IsBusy,
        bool AwaitingHumanDecision,
        bool SessionEnded,
        string? MessagingSocket,
        int? ClaudePid,
        int MalformedLines);

    [GeneratedRegex(@"\[atf-job:([A-Za-z0-9_-]{1,64})\]")]
    private static partial Regex TagPattern();

    static IReadOnlyList<string> ExtractTags(string? text) =>
        text is null ? [] : [.. TagPattern().Matches(text).Select(m => m.Groups[1].Value)];

    const int MaxResultChars = 4000;

    static readonly string[] PassThroughKeys = ["reason", "notification_type", "stop_hook_active", "tool_name", "source", "transcript_path"];

    /// <summary>Builds one redacted evidence line from a command-hook payload (legacy ClaudeHookRecord.Build).</summary>
    public static JsonObject BuildRecord(JsonObject payload, Func<string, string?> env, double timestamp, int pid, int parentPid)
    {
        var evt = Str(payload, "hook_event_name") ?? "";
        var record = new JsonObject
        {
            ["ts"] = timestamp,
            ["event"] = evt,
            ["session_id"] = Str(payload, "session_id"),
            ["prompt_id"] = Str(payload, "prompt_id"),
            ["cwd"] = Str(payload, "cwd"),
            ["pid"] = pid,
            ["ppid"] = parentPid,
            ["atf_agent"] = env("ATF_AGENT_NAME"),
        };

        if (evt == "SessionStart")
        {
            record["messaging_socket"] = env("CLAUDE_CODE_MESSAGING_SOCKET");
            record["has_messaging_token"] = !string.IsNullOrEmpty(env("CLAUDE_CODE_MESSAGING_TOKEN"));
        }

        if (Str(payload, "prompt") is { } prompt)
        {
            record["prompt_sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));
            record["prompt_len"] = prompt.Length;
            record["job_ids"] = new JsonArray([.. ExtractTags(prompt).Select(j => (JsonNode)j)]);
        }

        if (Str(payload, "last_assistant_message") is { } last)
        {
            record["last_assistant_message"] = last.Length > MaxResultChars ? last[..MaxResultChars] : last;
            record["last_assistant_truncated"] = last.Length > MaxResultChars;
        }

        foreach (var key in PassThroughKeys)
        {
            if (payload[key] is JsonValue value)
            {
                record[key] = value.DeepClone();
            }
        }
        return record;
    }

    /// <summary>
    /// Folds hook evidence for one session into prompt lifecycles (legacy ClaudeHookLog.Fold).
    /// Only Stop/StopFailure with the open turn's prompt_id closes it; other sessions and torn
    /// lines are ignored (torn lines are counted).
    /// </summary>
    public static ClaudeSessionView Fold(IEnumerable<string> lines, string sessionId)
    {
        var turns = new List<ClaudeTurn>();
        int? openIndex = null;
        var blocked = false;
        var ended = false;
        var malformed = 0;
        string? socket = null;
        int? claudePid = null;

        void CloseOpen(TurnStatus status, string? result)
        {
            if (openIndex is { } i)
            {
                turns[i] = turns[i] with { Status = status, Result = result };
                openIndex = null;
            }
            blocked = false;
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            JsonObject record;
            try
            {
                if (JsonNode.Parse(line) is not JsonObject o)
                {
                    malformed++;
                    continue;
                }
                record = o;
            }
            catch (JsonException)
            {
                malformed++;
                continue;
            }

            if (Str(record, "session_id") != sessionId)
            {
                continue;
            }

            var promptId = Str(record, "prompt_id");
            switch (Str(record, "event"))
            {
                case "SessionStart":
                    socket = Str(record, "messaging_socket") ?? socket;
                    claudePid = record["ppid"] is JsonValue p && p.TryGetValue<int>(out var pid) ? pid : claudePid;
                    ended = false;
                    break;
                case "UserPromptSubmit" when promptId is not null:
                    CloseOpen(TurnStatus.EndedWithoutResult, null);
                    var tags = record["job_ids"] is JsonArray ids ? ids.Select(n => n?.GetValue<string>()).OfType<string>().ToList() : [];
                    var ts = record["ts"] is JsonValue t && t.TryGetValue<double>(out var tv) ? tv : 0;
                    turns.Add(new ClaudeTurn(promptId, tags, Str(record, "prompt_sha256"), ts, TurnStatus.InProgress, null));
                    openIndex = turns.Count - 1;
                    break;
                case "Stop" when openIndex is { } i && turns[i].PromptId == promptId:
                    CloseOpen(TurnStatus.Completed, Str(record, "last_assistant_message"));
                    break;
                case "StopFailure" when openIndex is { } i && turns[i].PromptId == promptId:
                    CloseOpen(TurnStatus.Failed, Str(record, "reason"));
                    break;
                case "SessionEnd":
                    CloseOpen(TurnStatus.EndedWithoutResult, null);
                    ended = true;
                    break;
                case "Notification" when Str(record, "notification_type") == "permission_prompt":
                case "PermissionRequest":
                    blocked = true;
                    break;
                default:
                    break;
            }
        }

        return new ClaudeSessionView(turns, openIndex is not null, blocked, ended, socket, claudePid, malformed);
    }

    public static bool IsTerminal(JobState state) =>
        state is JobState.Completed or JobState.Interrupted or JobState.Failed;

    /// <summary>Legacy JobReconciler.Reconcile: maps observed turns to a job outcome.</summary>
    public static Reconciliation Reconcile(string jobId, JobState persisted, IEnumerable<ObservedTurn> observed,
        string? attemptId = null, string? boundTurnKey = null)
    {
        if (IsTerminal(persisted))
        {
            return new(persisted, null, null, "terminal state is final");
        }

        var matches = observed.Where(t => t.JobId == jobId).DistinctBy(t => t.TurnKey).ToList();
        if (matches.Count > 0 && persisted == JobState.Accepted)
        {
            return new(JobState.NeedsReconciliation, null, null, "backend shows this job id although it was never attempted");
        }
        if (matches.Any(t => attemptId is not null && t.AttemptId is not null && t.AttemptId != attemptId)
            || matches.Any(t => boundTurnKey is not null && t.TurnKey != boundTurnKey))
        {
            return new(JobState.NeedsReconciliation, null, null, "evidence belongs to another attempt or turn");
        }
        if (matches.Count > 1)
        {
            return new(JobState.NeedsReconciliation, null, null,
                $"duplicate delivery: {matches.Count} backend turns carry this job id", DuplicateDelivery: true);
        }

        if (matches.Count == 1)
        {
            var turn = matches[0];
            return turn.Status switch
            {
                TurnStatus.InProgress => new(JobState.BackendAcknowledged, turn.TurnKey, null, "backend turn in progress"),
                TurnStatus.Completed => new(JobState.Completed, turn.TurnKey, turn.ResultText, "authoritative completion observed"),
                TurnStatus.Interrupted => new(JobState.Interrupted, turn.TurnKey, turn.ResultText, "backend reported interruption"),
                TurnStatus.Failed => new(JobState.Failed, turn.TurnKey, turn.ResultText, "backend reported failure"),
                _ => new(JobState.NeedsReconciliation, turn.TurnKey, null, "turn ended without an authoritative completion signal"),
            };
        }

        return persisted switch
        {
            JobState.Accepted => new(JobState.Accepted, null, null, "not dispatched"),
            JobState.NeedsReconciliation => new(JobState.NeedsReconciliation, null, null, "still no backend evidence"),
            _ => new(JobState.NeedsReconciliation, null, null,
                "dispatch was attempted but no backend evidence exists; automatic redelivery is forbidden"),
        };
    }

    /// <summary>
    /// Legacy ClaudeCapabilityGate.NonAuthoritative: any outcome asserting backend evidence for the
    /// job becomes NeedsReconciliation without a result, because hooks cannot prove prompt origin.
    /// </summary>
    public static Reconciliation NonAuthoritative(Reconciliation r) =>
        r.TurnKey is null && !IsTerminal(r.State) && r.State != JobState.BackendAcknowledged
            ? r
            : new(JobState.NeedsReconciliation, r.TurnKey, null,
                $"Claude hook evidence cannot prove prompt origin; observed {r.State} is not authoritative");

    /// <summary>One hook evidence line built through <see cref="BuildRecord"/> from a raw hook payload.</summary>
    public static string HookLine(string evt, string sessionId, string? promptId = null, string? prompt = null, string? lastMessage = null, double ts = 10)
    {
        var payload = new JsonObject { ["hook_event_name"] = evt, ["session_id"] = sessionId };
        if (promptId is not null)
        {
            payload["prompt_id"] = promptId;
        }
        if (prompt is not null)
        {
            payload["prompt"] = prompt;
        }
        if (lastMessage is not null)
        {
            payload["last_assistant_message"] = lastMessage;
        }
        return BuildRecord(payload, _ => null, ts, pid: 10, parentPid: 9).ToJsonString();
    }

    static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
