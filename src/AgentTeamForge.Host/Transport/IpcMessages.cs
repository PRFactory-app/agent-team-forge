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
    public string? Workspace { get; init; }
    public string? BindingKey { get; init; }
    public bool AllWorkspace { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? Instruction { get; init; }
    public string? Behavior { get; init; }
    public bool Hold { get; init; }
    public string? JobId { get; init; }
    public string? Backend { get; init; }
    public string? Cwd { get; init; }
    public bool Worktree { get; init; }
    public bool Interrupt { get; init; }
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
    public int? OlderThanDays { get; init; }
    public bool DryRun { get; init; }
    public string? WakeKey { get; init; }
    public long? WakeGeneration { get; init; }
    public string? WakeKind { get; init; }
    public string? WakeAddress { get; init; }
    public string? WakeSecret { get; init; }
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

public sealed record IpcResponse(bool Ok, string? Error = null, string? Outcome = null, JobView? Job = null, JobListPage? Page = null, long? WakeGeneration = null, int? PrunedJobs = null, JobOutput? Output = null, AgentTeamForge.DAL.Features.Sessions.LeadSessionInfo? Session = null, AgentTeamForge.DAL.Features.External.JoinTicket? Ticket = null, AgentTeamForge.DAL.Features.External.JoinedMember? Member = null, AgentTeamForge.DAL.Features.External.ExternalInbox? Inbox = null, JobActivityPage? Activity = null, bool? AlreadyLeft = null, string? LeftName = null)
{
    // Flat aliases keep the external MCP replies usable by win-agent-teams skills.
    public bool Success => Ok;
    public string? Reason => Error;
    public string? SessionId => Ticket?.SessionId ?? Member?.SessionId;
    public string? Name => Ticket?.Name ?? Member?.Name ?? LeftName;
    public string? Token => Ticket?.Token;
    public string? JoinPrompt => Ticket?.JoinPrompt;
    public DateTimeOffset? ExpiresAt => Ticket?.ExpiresAt;
    public string? MemberToken => Member?.MemberToken;
    public IReadOnlyList<AgentTeamForge.DAL.Features.External.ExternalMessage>? Messages => Inbox?.Messages;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? Seq => Inbox?.SenderSeq;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public IReadOnlyDictionary<string, long>? Cursors => Inbox?.Cursors;
    public int? UnreadCount => Inbox?.UnreadCount;
    public bool? HasMore => Inbox?.HasMore;
}

public static class IpcProtocol
{
    public const int Version = 1;
    public const string Hello = "hello";
    public const string JobSubmit = "job_submit";
    public const string JobGet = "job_get";
    public const string JobOutput = "job_output";
    public const string JobActivity = "get_job_activity";
    public const string JobFollowUp = "job_follow_up";
    public const string JobStop = "job_stop";
    public const string JobStopAgent = "job_stop_agent";
    public const string JobList = "job_list";
    public const string JobPrune = "job_prune";
    public const string WakeRegister = "wake_register";
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
    public const string ExternalLeadRead = "external_lead_read";

    public const string UnsupportedVersion = "unsupported_version";
    public const string Unauthenticated = "unauthenticated";
    public const string IdentityMismatch = "identity_mismatch";
    public const string FrameTooLarge = "frame_too_large";
    public const string BadFrame = "bad_frame";
    public const string UnknownOp = "unknown_op";
    public const string DaemonUnavailable = "daemon_unavailable";
    public const string OutcomeUnknown = "outcome_unknown";
    public const string InternalError = "internal_error";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
[JsonSerializable(typeof(JobView))]
[JsonSerializable(typeof(JobListPage))]
[JsonSerializable(typeof(JobOutput))]
[JsonSerializable(typeof(JobActivityPage))]
[JsonSerializable(typeof(AgentTeamForge.DAL.Features.Sessions.LeadSessionInfo))]
[JsonSerializable(typeof(AgentTeamForge.DAL.Features.External.JoinTicket))]
[JsonSerializable(typeof(AgentTeamForge.DAL.Features.External.JoinedMember))]
[JsonSerializable(typeof(AgentTeamForge.DAL.Features.External.ExternalInbox))]
public sealed partial class IpcJson : JsonSerializerContext;
