using System.Security.Cryptography;
using System.Text;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>
/// Validates a submission, derives its semantic fingerprint, and durably
/// accepts it (or resolves the same key) before any acknowledgment. Nothing is
/// read or written once the admission gate is closed.
/// </summary>
public sealed class AcceptJob(JobStore store, BoundPrincipal principal, SpikeLimits limits, bool testProfile, AdmissionGate admission,
    IReadOnlyCollection<string>? backends = null, Func<string, IReadOnlyCollection<string>>? discoverModels = null, TierMap? tierMap = null,
    HerdrPlacement? herdrPlacement = null, Func<string, string?>? checkHerdrSession = null, string? launchMode = null)
{
    public const string Operation = "job_submit";

    const int MaxCwdChars = 4_096;

    readonly IReadOnlyCollection<string> _backends = backends ?? [BackendCatalog.Fake];

    public JobResult Execute(SubmitJobRequest request)
    {
        var behavior = request.Behavior ?? FakeBehavior.Complete;
        var backend = request.Backend ?? BackendCatalog.Fake;
        if (request.Instruction is { } instruction && InstructionError(instruction, backend) is { } instructionError)
        {
            return JobResult.Fail(instructionError);
        }
        if (!IsValid(request.IdempotencyKey, request.Instruction)
            || !FakeBehavior.All.Contains(behavior)
            || ((request.Hold || behavior != FakeBehavior.Complete) && !testProfile)
            || !ValidLimits(request.TimeoutSeconds, request.QueueTtlSeconds)
            || request.ExpectedOutputs is { } outputs && (outputs.Length > 100 || outputs.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 4096))
            || !ValidOption(request.Model) || !ValidOption(request.Effort)
            || request.TargetAgent is not null && !ValidAgentName(request.TargetAgent)
            || !TryNormalizeCwd(request.Cwd, out var cwd))
        {
            return JobResult.Fail(JobErrors.InvalidRequest);
        }

        if (!_backends.Contains(backend))
        {
            return JobResult.Fail(JobErrors.BackendUnavailable);
        }
        if (backend is BackendCatalog.Cursor or BackendCatalog.Droid && launchMode is not null and not "headless")
        {
            return JobResult.Fail($"{backend} is headless-only in ATF; choose --mode headless to run it");
        }

        var baseCommit = request.Worktree && cwd is not null ? JobWorktree.Head(cwd) : null;
        if (request.Worktree && baseCommit is null)
        {
            return JobResult.Fail(JobErrors.CwdNotGitRepo);
        }

        (string? model, string? effort) selection;
        try
        {
            selection = ResolveModel(backend, request.Model, request.Effort);
        }
        catch (ArgumentException ex)
        {
            return JobResult.Fail(ex.Message);
        }

        var options = $"behavior={behavior};hold={(request.Hold ? 1 : 0)};worktree={(request.Worktree ? 1 : 0)}";
        if (request.ExpectedOutputs is { Length: > 0 } paths)
        {
            options += ";expected_outputs=" + string.Join(",", paths.Select(p => Convert.ToBase64String(Encoding.UTF8.GetBytes(p))));
        }

        if (selection.model is not null)
        {
            options += $";model={selection.model}";
        }

        if (selection.effort is not null)
        {
            options += $";effort={selection.effort}";
        }

        if (backend is BackendCatalog.Claude or BackendCatalog.Codex or BackendCatalog.Pi && herdrPlacement is not null)
        {
            if (request.HerdrPlacement == "own-session") { return JobResult.Fail("herdr_placement own-session was removed; omit it to use the shared default session"); }
            string placement;
            try { placement = herdrPlacement.Resolve(request.HerdrPlacement); }
            catch (ArgumentException e) { return JobResult.Fail(e.Message); }
            if (placement.StartsWith("herdr-session:", StringComparison.Ordinal) && checkHerdrSession?.Invoke(placement[14..]) is { } problem)
            {
                return JobResult.Fail(problem);
            }
            options += ";herdr_placement=" + placement;
        }
        else if (request.HerdrPlacement is not null) { return JobResult.Fail("Herdr placement requires Herdr launch mode"); }

        return Admit(Operation, request.IdempotencyKey, request.Instruction, options, backend,
            cwd, null, request.WakeKey, request.WakeGeneration, request.Worktree, baseCommit,
            timeoutSeconds: request.TimeoutSeconds, queueTtlSeconds: request.QueueTtlSeconds, leadSessionId: request.LeadSessionId,
            targetAgent: request.TargetAgent, defaultAgent: backend + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.IdempotencyKey)))[..8]);
    }

    /// <summary>Optional job timeout and queue TTL: whole seconds, at most one day.</summary>
    internal static bool ValidLimits(int? timeoutSeconds, int? queueTtlSeconds) =>
        timeoutSeconds is null or (>= 1 and <= MaxLimitSeconds) && queueTtlSeconds is null or (>= 1 and <= MaxLimitSeconds);

    const int MaxLimitSeconds = 86_400;

    // These values become CLI arguments (and on Windows may pass through a command shim).
    public static bool ValidOption(string? value) => value is null ||
        (value.Length is > 0 and <= 128 && value[0] != '-' && !value.Any(c => char.IsControl(c) || c is ';' or '=' or '"' or '\'' or '`' or '$' or '&' or '|' or '<' or '>'));

    public static bool ValidAgentName(string name) => name.Length is > 0 and <= 64 && char.IsAsciiLetterOrDigit(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    internal (string? model, string? effort) ResolveModel(string backend, string? model, string? effort) =>
        ModelSelection.Resolve(backend, model, effort, discoverModels, tierMap);

    internal bool IsValid(string? key, string? instruction) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= limits.MaxIdempotencyKeyChars
        && !string.IsNullOrEmpty(instruction);

    internal string? InstructionError(string instruction, string? backend)
    {
        if (instruction.Length > limits.MaxInstructionChars) { return JobErrors.InstructionTooLong; }
        if (backend is not (BackendCatalog.Claude or BackendCatalog.Codex or BackendCatalog.Pi)) { return null; }
        // Linux limits one execve argument to 128 KiB; Herdr accepts only a positional TEXT argument.
        // Leave room for the correlation marker appended by the interactive backend.
        if (herdrPlacement is not null && Encoding.UTF8.GetByteCount(instruction) > 120 * 1024)
        {
            return JobErrors.HerdrPromptTooLarge;
        }
        // Windows tabs hand an over-long prompt over as a private file; macOS has no per-argument limit.
        return null;
    }

    /// <summary>Durable acceptance shared by submit and follow-up; one admission-gated transaction.</summary>
    internal JobResult Admit(string operation, string key, string instruction, string options, string backend, string? cwd, string? parentJobId,
        string? wakeKey = null, long? wakeGeneration = null, bool createWorktree = false, string? worktreeBase = null,
        string? worktreePath = null, string? worktreeBranch = null, int? timeoutSeconds = null, int? queueTtlSeconds = null,
        bool interruptParent = false, Action<string>? cancelRunning = null, string? leadSessionId = null, string? targetAgent = null, string? defaultAgent = null, bool deferParent = false)
    {
        if (!admission.TryEnter())
        {
            return JobResult.Fail(JobErrors.DaemonUnhealthy);
        }

        try
        {
            // Unnamed jobs keep the principal in the fingerprint so retries of jobs accepted before derived names still match.
            var agent = targetAgent ?? defaultAgent ?? principal.Agent;
            string[] fields = [targetAgent ?? principal.Agent, instruction, options, backend, cwd ?? string.Empty, parentJobId ?? string.Empty];
            if (timeoutSeconds is not null || queueTtlSeconds is not null)
            {
                // Only when set, so fingerprints of jobs accepted before these limits existed are unchanged.
                fields = [.. fields, $"timeout={timeoutSeconds};queue_ttl={queueTtlSeconds}"];
            }

            var job = new NewJob(principal.Principal, principal.Team, agent, operation, leadSessionId is null ? key : leadSessionId + ":" + key, Fingerprint(fields), instruction, options)
            {
                TimeoutSeconds = timeoutSeconds,
                QueueTtlSeconds = queueTtlSeconds,
                Backend = backend,
                Cwd = cwd,
                ParentJobId = parentJobId,
                InterruptParent = interruptParent,
                DeferParent = deferParent,
                CreateWorktree = createWorktree,
                WorktreeBase = worktreeBase,
                WorktreePath = worktreePath,
                WorktreeBranch = worktreeBranch,
                LeadSessionId = leadSessionId,
                WakeTargetKey = wakeKey,
                WakeGeneration = wakeGeneration,
            };

            AcceptOutcome outcome;
            try
            {
                outcome = store.AcceptOrGet(job, limits.QueueLimit);
            }
            catch (StorageException ex)
            {
                return JobResult.Fail(JobErrors.FromStorage(ex));
            }

            if (outcome is Accepted { InterruptedJobId: { } interrupted })
            {
                // The cancellation and child intent are already durable. The
                // daemon holds the claim gate until this owned run is stopped.
                cancelRunning?.Invoke(interrupted);
            }

            return outcome switch
            {
                Accepted accepted => JobResult.Ok(GetJob.ToView(accepted.Job), "accepted"),
                Existing existing => JobResult.Ok(GetJob.ToView(existing.Job), "existing"),
                Conflict => JobResult.Fail(JobErrors.IdempotencyConflict),
                ParentNotReady => JobResult.Fail(JobErrors.ParentNotReady),
                ParentNotFound => JobResult.Fail(JobErrors.NotFound),
                QueueFull => JobResult.Fail(JobErrors.QueueFull),
            };
        }
        finally
        {
            admission.Exit();
        }
    }

    static bool TryNormalizeCwd(string? cwd, out string? normalized)
    {
        normalized = null;
        if (cwd is null)
        {
            return true;
        }

        if (cwd.Length > MaxCwdChars || !Path.IsPathFullyQualified(cwd) || !Directory.Exists(cwd))
        {
            return false;
        }

        normalized = Path.GetFullPath(cwd);
        return true;
    }

    /// <summary>
    /// Stable hash of validated semantic fields only (target, instruction,
    /// execution options, backend, cwd, parent), length-prefixed so field
    /// boundaries cannot collide. Transport request IDs and JSON property order never participate.
    /// </summary>
    internal static string Fingerprint(params string[] fields)
    {
        var canonical = new StringBuilder("v1");
        foreach (var field in fields)
        {
            canonical.Append('|').Append(field.Length).Append(':').Append(field);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
