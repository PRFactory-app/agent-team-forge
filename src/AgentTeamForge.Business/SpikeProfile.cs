namespace AgentTeamForge.Business;

/// <summary>
/// The one configured operator principal/team/agent this spike daemon serves.
/// Identity is bound by configuration, never taken from request payloads.
/// </summary>
public sealed record BoundPrincipal(string Principal, string Team, string Agent);

/// <summary>All spike bounds in one place; recorded in evidence manifests.</summary>
public sealed record SpikeLimits
{
    public int MaxFrameBytes { get; init; } = 64 * 1024;
    public int MaxIdempotencyKeyChars { get; init; } = 128;
    public int MaxInstructionChars { get; init; } = 4_000;
    public int MaxResultChars { get; init; } = 16_000;
    public int MaxBackendLineBytes { get; init; } = 32 * 1024;
    public int QueueLimit { get; init; } = 8;
    public TimeSpan MaxFakeRuntime { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan FrameReadTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
