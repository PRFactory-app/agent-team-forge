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
    public string? IdempotencyKey { get; init; }
    public string? Instruction { get; init; }
    public string? Behavior { get; init; }
    public bool Hold { get; init; }
    public string? JobId { get; init; }
    public string? Backend { get; init; }
    public string? Cwd { get; init; }
}

public sealed record IpcResponse(bool Ok, string? Error = null, string? Outcome = null, JobView? Job = null, IReadOnlyList<JobView>? Jobs = null);

public static class IpcProtocol
{
    public const int Version = 1;
    public const string Hello = "hello";
    public const string JobSubmit = "job_submit";
    public const string JobGet = "job_get";
    public const string JobFollowUp = "job_follow_up";
    public const string JobList = "job_list";

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
public sealed partial class IpcJson : JsonSerializerContext;
