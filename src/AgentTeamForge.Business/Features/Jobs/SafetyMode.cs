using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Per-job launch mode (ADR 0013): the requested mode is the caller's intent, the effective mode what the worker enforces.</summary>
public static class SafetyMode
{
    public const string Sandboxed = "sandboxed";
    public const string FullAccess = "fullAccess";

    public static bool Valid(string? value) => value is Sandboxed or FullAccess;
}

/// <summary>Why a Sandboxed request runs with full access. Stable codes, as the PRFactory acceptance report names them.</summary>
public static class SafetyDowngrade
{
    /// <summary>The backend has no enforceable sandbox at all (Pi, Cursor, Droid).</summary>
    public const string UnsupportedBackend = "sandbox_unsupported_backend";

    /// <summary>The backend has a sandbox this machine cannot enforce (native Windows Claude, an unverified OS, a Codex user config that widens the sandbox).</summary>
    public const string Unavailable = "sandbox_unavailable";
}

/// <summary>
/// Decides whether this machine can enforce a Sandboxed launch for one backend: null means it can, otherwise the downgrade reason.
/// The startup self-test of work item A3 replaces <see cref="SandboxCapability.Default"/> without touching callers.
/// </summary>
public delegate string? SandboxProbe(string backend, string? cwd, string? launchMode);

public static class SandboxCapability
{
    /// <summary>
    /// Verified on Linux only (ADR 0013 spike); macOS and Windows stay downgraded until the live run proves them. An interactive launch
    /// (Herdr, Windows Terminal, macOS terminal) is always downgraded: the pane outlives its turn and ATF cannot prove when it ends, so
    /// daemon git could race a live writer, and a Herdr pane cannot be started through an environment allow-list (the shell's startup
    /// files run first, and Herdr launches the canonical executable itself).
    /// </summary>
    public static string? Default(string backend, string? cwd, string? launchMode) => backend switch
    {
        BackendCatalog.Claude or BackendCatalog.Codex => OperatingSystem.IsLinux() && launchMode is null or "headless" ? null : SafetyDowngrade.Unavailable,
        _ => SafetyDowngrade.UnsupportedBackend,
    };
}

/// <summary>The requested and effective mode of one job, stored in its options string: <c>sandbox=1;sandbox_effective=1|0[;sandbox_downgrade=code]</c>.</summary>
public sealed record JobSafety(string Requested, string Effective, string? DowngradeReason)
{
    public static readonly JobSafety FullAccess = new(SafetyMode.FullAccess, SafetyMode.FullAccess, null);

    public bool RequestedSandboxed => Requested == SafetyMode.Sandboxed;
    public bool EffectiveSandboxed => Effective == SafetyMode.Sandboxed;

    /// <summary>A job without the keys (every job before this option, and every full-access job) is full access.</summary>
    public static JobSafety Read(string? options)
    {
        options ??= "";
        if (JobOptions.Read(options, "sandbox") != "1") { return FullAccess; }
        var effective = JobOptions.Read(options, "sandbox_effective") == "1" ? SafetyMode.Sandboxed : SafetyMode.FullAccess;
        return new(SafetyMode.Sandboxed, effective, JobOptions.Read(options, "sandbox_downgrade"));
    }

    /// <summary>Requested sandboxed: the enforceable effective mode, or full access with the reason. Requested full access stores nothing.</summary>
    public static JobSafety Decide(string requested, string backend, string? cwd, string? launchMode, SandboxProbe probe)
    {
        if (requested != SafetyMode.Sandboxed) { return FullAccess; }
        var reason = probe(backend, cwd, launchMode);
        return reason is null ? new(SafetyMode.Sandboxed, SafetyMode.Sandboxed, null) : new(SafetyMode.Sandboxed, SafetyMode.FullAccess, reason);
    }

    /// <summary>Options fragment, empty for full access so fingerprints of jobs accepted before this option stay unchanged.</summary>
    public string ToOptions() => !RequestedSandboxed ? "" : ";sandbox=1;sandbox_effective=" + (EffectiveSandboxed ? "1" : "0")
        + (DowngradeReason is { } reason ? ";sandbox_downgrade=" + reason : "");
}
