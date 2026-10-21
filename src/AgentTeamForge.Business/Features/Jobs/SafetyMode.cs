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
public delegate string? SandboxProbe(string backend, string? cwd, bool interactive);

public static class SandboxCapability
{
    /// <summary>Verified on Linux only (ADR 0013 spike); macOS and Windows stay downgraded until the live run proves them.</summary>
    public static string? Default(string backend, string? cwd, bool interactive) => backend switch
    {
        BackendCatalog.Claude => OperatingSystem.IsLinux() ? null : SafetyDowngrade.Unavailable,
        // `codex exec` ignores the user config (--ignore-user-config). The interactive CLI has no such flag, so there the user config
        // and its project trust would still apply: fail closed whenever the file has content (ADR 0013, "Settings sources").
        BackendCatalog.Codex => OperatingSystem.IsLinux() && !(interactive && CodexConfigCheck.HasUserConfig()) ? null : SafetyDowngrade.Unavailable,
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
    public static JobSafety Decide(string requested, string backend, string? cwd, bool interactive, SandboxProbe probe)
    {
        if (requested != SafetyMode.Sandboxed) { return FullAccess; }
        var reason = probe(backend, cwd, interactive);
        return reason is null ? new(SafetyMode.Sandboxed, SafetyMode.Sandboxed, null) : new(SafetyMode.Sandboxed, SafetyMode.FullAccess, reason);
    }

    /// <summary>Options fragment, empty for full access so fingerprints of jobs accepted before this option stay unchanged.</summary>
    public string ToOptions() => !RequestedSandboxed ? "" : ";sandbox=1;sandbox_effective=" + (EffectiveSandboxed ? "1" : "0")
        + (DowngradeReason is { } reason ? ";sandbox_downgrade=" + reason : "");
}
