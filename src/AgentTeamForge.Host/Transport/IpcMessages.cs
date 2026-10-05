using System.Text.Json.Serialization;
using AgentTeamForge.Business.Features.Jobs;

namespace AgentTeamForge.Host.Transport;

/// <summary>Private daemon IPC envelope, protocol version 1.</summary>
public sealed record IpcRequest
{
    public int ProtocolVersion { get; init; }
    public required string Op { get; init; }
    public string? Credential { get; init; }
    public string? Principal { get; init; }
    public string? LeadSessionId { get; init; }
    public string? SessionName { get; init; }
    public string? Workspace { get; init; }
    public string? BindingKey { get; init; }
    public bool AllWorkspace { get; init; }
    public bool Unread { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? Instruction { get; init; }
    public string? Behavior { get; init; }
    public bool Hold { get; init; }
    public string? JobId { get; init; }
    public string? Backend { get; init; }
    public string? TargetAgent { get; init; }
    public string[]? ExpectedOutputs { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? HerdrPlacement { get; init; }
    public string? Tier { get; init; }
    public int? MaxRetainedSessions { get; init; }
    public int? IdleCloseMinutes { get; init; }
    public bool ResetAllTiers { get; init; }
    public string? Cwd { get; init; }
    public bool Worktree { get; init; }
    public bool Interrupt { get; init; }
    public bool Defer { get; init; }
    public bool ReplaceIfIdle { get; init; } = true;
    public int? TimeoutSeconds { get; init; }
    public int? QueueTtlSeconds { get; init; }
    public string? Status { get; init; }
    public string? Since { get; init; }
    public int? Limit { get; init; }
    public long? Offset { get; init; }
    public long? AfterCursor { get; init; }
    public int? MaxBytes { get; init; }
    public string? Cursor { get; init; }
    public bool OrderByActivity { get; init; }
    public bool IncludeConnector { get; init; }
    public bool IncludeUsage { get; init; }
    public bool ExcludeArchived { get; init; }
    public bool IncludeInstruction { get; init; }
    public string? NativeKind { get; init; }
    public string? NativeSessionId { get; init; }
    public string? NativeHome { get; init; }
    public int? OlderThanDays { get; init; }
    public bool DryRun { get; init; }
    public bool Force { get; init; }
    public string? WakeKey { get; init; }
    public long? WakeGeneration { get; init; }
    public string? WakeKind { get; init; }
    public string? WakeAddress { get; init; }
    public string? WakeSecret { get; init; }
    public string? NoticeId { get; init; }
    public bool NoticePosted { get; init; }
    public bool NativeWriteStarted { get; init; }
    public string? NativeCorrelation { get; init; }
    public string? NativeRunId { get; init; }
    public string? WakeHome { get; init; }
    public string? MemberName { get; init; }
    public string? MemberToken { get; init; }
    public string? TicketToken { get; init; }
    public string? Note { get; init; }
    public string? Text { get; init; }
    public long? SinceSeq { get; init; }
    public string? FromAgent { get; init; }
    public bool Full { get; init; }
    public int? MaxChars { get; init; }
    public string? CodexThreadId { get; init; }
}

public sealed record IpcResponse(bool Ok, string? Error = null, string? Outcome = null, JobView? Job = null, JobListPage? Page = null, long? WakeGeneration = null, int? PrunedJobs = null, JobOutput? Output = null, AgentTeamForge.DAL.Features.Sessions.LeadSessionInfo? Session = null, AgentTeamForge.DAL.Features.External.JoinTicket? Ticket = null, AgentTeamForge.DAL.Features.External.JoinedMember? Member = null, AgentTeamForge.DAL.Features.External.ExternalInbox? Inbox = null, JobActivityPage? Activity = null, bool? AlreadyLeft = null, string? LeftName = null, IReadOnlyCollection<string>? Backends = null, IReadOnlyDictionary<string, AgentModelOptions>? ModelOptions = null, string? ErrorDetail = null, IReadOnlyList<TierSetting>? Tiers = null, IReadOnlyDictionary<string, IReadOnlyCollection<string>>? ModelCatalog = null, string? HerdrPlacement = null, bool? HerdrMode = null, AgentTeamForge.DAL.Features.Wake.WakeRegistrationStatus? WakeStatus = null, IReadOnlyDictionary<string, bool>? BackendAvailability = null, IReadOnlyDictionary<string, bool>? BackendInstalled = null, IReadOnlyDictionary<string, string>? BackendSignIn = null, string? LaunchMode = null, AgentTeamForge.Business.Features.Wake.ClaudeWakeNotice? ClaudeNotice = null, string? Instruction = null, IReadOnlyList<AgentTeamForge.DAL.Features.External.ExternalMemberSummary>? ExternalMembers = null, NativeClaudeOffer? ClaudeDelivery = null, IReadOnlyList<WorktreeCleanupResult>? Worktrees = null, AgentTeamForge.Business.Features.Agents.Terminals.InteractiveRetentionSettings? RetentionSettings = null, IReadOnlyDictionary<string, AgentTeamForge.Business.Features.Usage.TokenUsage?>? LeadTokens = null, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>? ModelEfforts = null, IReadOnlyDictionary<string, int>? Counts = null)
{
    public string? FencingJobId { get; init; }
    public JobRecovery? Recovery { get; init; }
    public string? OwnerLead { get; init; }
    public DateTimeOffset? ReachExpiresAt { get; init; }

    // Flat aliases keep the external MCP replies usable by win-agent-teams skills.
    public bool Success => Ok;
    public string? Reason => Error;
    public string? SessionId => Ticket?.SessionId ?? Member?.SessionId;
    public string? Name => Ticket?.Name ?? Member?.Name ?? LeftName;
    public string? Token => Ticket?.Token;
    public string? JoinPrompt => Ticket?.JoinPrompt ?? Member?.JoinPrompt;
    public DateTimeOffset? ExpiresAt => Ticket?.ExpiresAt;
    public string? MemberToken => Member?.MemberToken;
    // Inbox fields are flat on the MCP wire (see ForMcp); the nested Inbox is only used daemon->bridge.
    AgentTeamForge.DAL.Features.External.ExternalInbox? flat;
    public IReadOnlyList<AgentTeamForge.DAL.Features.External.ExternalMessage>? Messages { get => flat?.Messages ?? field ?? Inbox?.Messages; init; }
    public long? NextSeq { get => flat?.NextSeq ?? field ?? Inbox?.NextSeq; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? Seq { get => flat is not null ? flat.SenderSeq : field ?? Inbox?.SenderSeq; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public IReadOnlyDictionary<string, long>? Cursors { get => flat is not null ? flat.Cursors : field ?? Inbox?.Cursors; init; }
    public int? UnreadCount { get => flat?.UnreadCount ?? field ?? Inbox?.UnreadCount; init; }
    public bool? HasMore { get => flat?.HasMore ?? field ?? Inbox?.HasMore; init; }

    /// <summary>MCP-facing copy: inbox fields appear once, at top level, instead of also nested under <c>inbox</c>.</summary>
    public IpcResponse ForMcp() => Inbox is null ? this : (this with { Inbox = null }).WithFlat(Inbox);

    IpcResponse WithFlat(AgentTeamForge.DAL.Features.External.ExternalInbox inbox)
    {
        flat = inbox;
        return this;
    }
}

public static class IpcProtocol
{
    public const int Version = 1;
    public const string Hello = "hello";
    public const string JobSubmit = "job_submit";
    public const string JobCapabilities = "job_capabilities";
    public const string RetentionSettingsGet = "retention_settings_get";
    public const string RetentionSettingsPut = "retention_settings_put";
    public const string TierSettingsGet = "tier_settings_get";
    public const string TierSettingsPut = "tier_settings_put";
    public const string HerdrPlacementGet = "herdr_placement_get";
    public const string HerdrPlacementPut = "herdr_placement_put";
    public const string JobGet = "job_get";
    public const string JobOutput = "job_output";
    public const string JobActivity = "get_job_activity";
    public const string JobFollowUp = "job_follow_up";
    public const string JobRelease = "job_release";
    public const string JobStop = "job_stop";
    public const string JobStopAgent = "job_stop_agent";
    public const string JobStopIdle = "job_stop_idle";
    public const string JobStopIdleStatus = "job_stop_idle_status";
    public const string JobArchiveFinished = "job_archive_finished";
    public const string JobList = "job_list";
    public const string JobPrune = "job_prune";
    public const string JobRemoveWorktree = "job_remove_worktree";
    public const string JobPruneWorktrees = "job_prune_worktrees";
    public const string ClaudeWakeTake = "claude_wake_take";
    public const string ClaudeWakeComplete = "claude_wake_complete";
    public const string ClaudeDeliveryTake = "claude_delivery_take";
    public const string ClaudeDeliveryComplete = "claude_delivery_complete";
    public const string WakeRegister = "wake_register";
    public const string WakeClear = "wake_clear";
    public const string WakeStatus = "wake_status";
    public const string SessionStart = "session_start";
    public const string SessionInfo = "session_info";
    public const string SessionResume = "session_resume";
    public const string SessionBindWake = "session_bind_wake";
    public const string SessionClose = "session_close";
    public const string ExternalTicket = "external_ticket";
    public const string ExternalJoin = "external_join";
    public const string ExternalSend = "external_send";
    public const string ExternalRead = "external_read";
    public const string ExternalSetWake = "external_set_wake";
    public const string ExternalLeave = "external_leave";
    public const string ExternalLeadSend = "external_lead_send";
    public const string ExternalOperatorSend = "external_operator_send";
    public const string ExternalLeadRead = "external_lead_read";
    public const string HumanInputRequest = "human_input_request";

    public const string UnsupportedVersion = "unsupported_version";
    public const string Unauthenticated = "unauthenticated";
    public const string IdentityMismatch = "identity_mismatch";
    public const string FrameTooLarge = "frame_too_large";
    public const string BadFrame = "bad_frame";
    public const string UnknownOp = "unknown_op";
    public const string DaemonUnavailable = "daemon_unavailable";
    /// <summary>The daemon is alive but every request slot stayed taken; nothing was sent, so a retry is safe.</summary>
    public const string DaemonBusy = "daemon_busy";
    public const string AccessDenied = "access_denied";
    public const string OutcomeUnknown = "outcome_unknown";
    public const string InternalError = "internal_error";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NativeClaudeOffer))]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
[JsonSerializable(typeof(JobView))]
[JsonSerializable(typeof(WorktreeCleanupResult))]
[JsonSerializable(typeof(AgentTeamForge.Business.Features.Usage.TokenUsage))]
[JsonSerializable(typeof(JobListPage))]
[JsonSerializable(typeof(JobOutput))]
[JsonSerializable(typeof(JobActivityPage))]
[JsonSerializable(typeof(AgentTeamForge.DAL.Features.Sessions.LeadSessionInfo))]
[JsonSerializable(typeof(AgentTeamForge.DAL.Features.External.JoinTicket))]
[JsonSerializable(typeof(AgentTeamForge.DAL.Features.External.JoinedMember))]
[JsonSerializable(typeof(AgentTeamForge.DAL.Features.External.ExternalInbox))]
[JsonSerializable(typeof(AgentTeamForge.DAL.Features.External.ExternalMemberSummary))]
public sealed partial class IpcJson : JsonSerializerContext;

public sealed record NativeClaudeOffer(string JobId, string RunId, string Correlation, string Instruction);
