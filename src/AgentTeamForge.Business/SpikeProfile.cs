namespace AgentTeamForge.Business;

/// <summary>
/// The one configured operator principal/team/agent this spike daemon serves.
/// Identity is bound by configuration, never taken from request payloads.
/// </summary>
public sealed record BoundPrincipal(string Principal, string Team, string Agent);

/// <summary>All spike bounds in one place; recorded in evidence manifests.</summary>
public sealed record SpikeLimits
{
    // Room for a max-size result even if every char is JSON-escaped (6 bytes).
    public int MaxFrameBytes { get; init; } = 2 * 1024 * 1024;
    public int MaxIdempotencyKeyChars { get; init; } = 128;
    public int MaxInstructionChars { get; init; } = 4_000;
    public int MaxResultChars { get; init; } = 256 * 1024;
    public int MaxBackendLineBytes { get; init; } = 2 * 1024 * 1024;
    public int QueueLimit { get; init; } = 64;
    public int MaxConcurrentJobs { get; init; } = 8;
    public TimeSpan MaxFakeRuntime { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan FrameReadTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
