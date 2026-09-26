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
    IReadOnlyCollection<string>? backends = null)
{
    public const string Operation = "job_submit";

    const int MaxCwdChars = 4_096;

    readonly IReadOnlyCollection<string> _backends = backends ?? [BackendCatalog.Fake];

    public JobResult Execute(SubmitJobRequest request)
    {
        var behavior = request.Behavior ?? FakeBehavior.Complete;
        var backend = request.Backend ?? BackendCatalog.Fake;
        if (!IsValid(request.IdempotencyKey, request.Instruction)
            || !FakeBehavior.All.Contains(behavior)
            || ((request.Hold || behavior != FakeBehavior.Complete) && !testProfile)
            || !TryNormalizeCwd(request.Cwd, out var cwd))
        {
            return JobResult.Fail(JobErrors.InvalidRequest);
        }

        if (!_backends.Contains(backend))
        {
            return JobResult.Fail(JobErrors.BackendUnavailable);
        }

        var baseCommit = request.Worktree && cwd is not null ? JobWorktree.Head(cwd) : null;
        if (request.Worktree && baseCommit is null)
        {
            return JobResult.Fail(JobErrors.CwdNotGitRepo);
        }

        var options = $"behavior={behavior};hold={(request.Hold ? 1 : 0)};worktree={(request.Worktree ? 1 : 0)}";
        return Admit(Operation, request.IdempotencyKey, request.Instruction, options, backend,
            cwd, null, request.WakeKey, request.WakeGeneration, request.Worktree, baseCommit);
    }

    internal bool IsValid(string? key, string? instruction) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= limits.MaxIdempotencyKeyChars
        && !string.IsNullOrEmpty(instruction) && instruction.Length <= limits.MaxInstructionChars;

    /// <summary>Durable acceptance shared by submit and follow-up; one admission-gated transaction.</summary>
    internal JobResult Admit(string operation, string key, string instruction, string options, string backend, string? cwd, string? parentJobId,
        string? wakeKey = null, long? wakeGeneration = null, bool createWorktree = false, string? worktreeBase = null,
        string? worktreePath = null, string? worktreeBranch = null)
    {
        if (!admission.TryEnter())
        {
            return JobResult.Fail(JobErrors.DaemonUnhealthy);
        }

        try
        {
            var job = new NewJob(principal.Principal, principal.Team, principal.Agent, operation, key,
                Fingerprint(principal.Agent, instruction, options, backend, cwd ?? string.Empty, parentJobId ?? string.Empty), instruction, options)
            {
                Backend = backend,
                Cwd = cwd,
                ParentJobId = parentJobId,
                CreateWorktree = createWorktree,
                WorktreeBase = worktreeBase,
                WorktreePath = worktreePath,
                WorktreeBranch = worktreeBranch,
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

            switch (outcome.Kind)
            {
                case AcceptKind.Accepted:
                    return JobResult.Ok(GetJob.ToView(outcome.Job!), "accepted");
                case AcceptKind.Existing:
                    return JobResult.Ok(GetJob.ToView(outcome.Job!), "existing");
                case AcceptKind.Conflict:
                    return JobResult.Fail(JobErrors.IdempotencyConflict);
                default:
                    return JobResult.Fail(JobErrors.QueueFull);
            }
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
