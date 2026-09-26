using System.Security.Cryptography;
using System.Text;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>
/// Validates a submission, derives its semantic fingerprint, and durably
/// accepts it (or resolves the same key) before any acknowledgment. Nothing is
/// read or written once the admission gate is closed.
/// </summary>
public sealed class AcceptJob(JobStore store, BoundPrincipal principal, SpikeLimits limits, bool testProfile, AdmissionGate admission)
{
    public const string Operation = "job_submit";

    public JobResult Execute(SubmitJobRequest request)
    {
        var behavior = request.Behavior ?? FakeBehavior.Complete;
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > limits.MaxIdempotencyKeyChars
            || request.Instruction is null || request.Instruction.Length == 0 || request.Instruction.Length > limits.MaxInstructionChars
            || !FakeBehavior.All.Contains(behavior)
            || ((request.Hold || behavior != FakeBehavior.Complete) && !testProfile))
        {
            return JobResult.Fail(JobErrors.InvalidRequest);
        }

        if (!admission.TryEnter())
        {
            return JobResult.Fail(JobErrors.DaemonUnhealthy);
        }

        try
        {
            return Admit(request, behavior);
        }
        finally
        {
            admission.Exit();
        }
    }

    JobResult Admit(SubmitJobRequest request, string behavior)
    {
        var options = $"behavior={behavior};hold={(request.Hold ? 1 : 0)}";
        var job = new NewJob(principal.Principal, principal.Team, principal.Agent, Operation, request.IdempotencyKey,
            Fingerprint(principal.Agent, request.Instruction, options), request.Instruction, options);

        AcceptOutcome outcome;
        try
        {
            outcome = store.AcceptOrGet(job, limits.QueueLimit);
        }
        catch (StorageException ex)
        {
            return JobResult.Fail(ex.Failure == StorageFailure.Busy ? JobErrors.StorageBusy : JobErrors.StorageUnavailable);
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

    /// <summary>
    /// Stable hash of validated semantic fields only (target, instruction,
    /// execution options), length-prefixed so field boundaries cannot collide.
    /// Transport request IDs and JSON property order never participate.
    /// </summary>
    internal static string Fingerprint(string target, string instruction, string options)
    {
        var canonical = new StringBuilder("v1");
        foreach (var field in new[] { target, instruction, options })
        {
            canonical.Append('|').Append(field.Length).Append(':').Append(field);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
