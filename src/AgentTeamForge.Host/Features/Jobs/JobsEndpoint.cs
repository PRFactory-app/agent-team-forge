using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Jobs;

/// <summary>Thin IPC mapping for the job operations; all rules live in Business.</summary>
public sealed class JobsEndpoint(AcceptJob accept, GetJob get, FollowUpJob followUp, ListJobs list, StopJob stop, DurabilityCheckpoints checkpoints, Action onAccepted,
    WakeStore? wakeStore = null, PruneJob? prune = null)
{
    public IpcResponse Handle(IpcRequest request)
    {
        switch (request.Op)
        {
            case IpcProtocol.JobSubmit:
                return Accepted(accept.Execute(new SubmitJobRequest(request.IdempotencyKey ?? string.Empty, request.Instruction ?? string.Empty, request.Behavior, request.Hold)
                {
                    Backend = request.Backend,
                    Cwd = request.Cwd,
                    Worktree = request.Worktree,
                    TimeoutSeconds = request.TimeoutSeconds,
                    QueueTtlSeconds = request.QueueTtlSeconds,
                    WakeKey = request.WakeKey,
                    WakeGeneration = request.WakeGeneration,
                }));
            case IpcProtocol.JobFollowUp:
                return Accepted(followUp.Execute(new FollowUpRequest(request.JobId ?? string.Empty, request.Instruction ?? string.Empty, request.IdempotencyKey ?? string.Empty)
                {
                    TimeoutSeconds = request.TimeoutSeconds,
                    QueueTtlSeconds = request.QueueTtlSeconds,
                    WakeKey = request.WakeKey,
                    WakeGeneration = request.WakeGeneration,
                }));
            case IpcProtocol.JobStop:
                return Map(stop.Execute(request.JobId ?? string.Empty));
            case IpcProtocol.JobGet:
                var found = get.Execute(request.JobId ?? string.Empty);
                if (found.Error is null && request.WakeKey is not null && request.WakeGeneration is long generation && wakeStore is not null)
                {
                    wakeStore.MarkRead(request.JobId!, request.WakeKey, generation);
                }
                return Map(found);
            case IpcProtocol.JobList:
                var listed = list.Execute(new ListJobsRequest(request.Status, request.Limit, request.Cursor));
                return listed.Error is null ? new IpcResponse(true, Outcome: "listed", Page: listed.Page) : new IpcResponse(false, listed.Error);
            case IpcProtocol.JobPrune:
                if (prune is null || request.OlderThanDays is not (>= 1 and <= 36500))
                {
                    return new IpcResponse(false, JobErrors.InvalidRequest);
                }
                return new IpcResponse(true, Outcome: request.DryRun ? "dry_run" : "pruned",
                    PrunedJobs: prune.Execute(request.OlderThanDays.Value, request.DryRun));
            case IpcProtocol.WakeRegister:
                if (wakeStore is null || string.IsNullOrWhiteSpace(request.WakeKey) || request.WakeKey.Length > 256
                    || request.WakeKind is not ("claude" or "codex" or "pi") || string.IsNullOrWhiteSpace(request.WakeAddress)
                    || request.WakeAddress.Length > 4096 || (request.WakeSecret?.Length ?? 0) > 4096
                    || (request.WakeHome?.Length ?? 0) > 4096)
                {
                    return new IpcResponse(false, JobErrors.InvalidRequest);
                }
                var registration = wakeStore.Register(request.WakeKey, request.WakeKind, request.WakeAddress,
                    request.WakeSecret ?? string.Empty, request.WakeHome ?? string.Empty);
                return new IpcResponse(true, Outcome: "registered", WakeGeneration: registration.Generation);
            default:
                return new IpcResponse(false, IpcProtocol.UnknownOp);
        }
    }

    IpcResponse Accepted(JobResult result)
    {
        if (result.Outcome == "accepted")
        {
            try
            {
                checkpoints.Hit(DurabilityCheckpoints.AcceptAfterCommit);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return new IpcResponse(false, IpcProtocol.OutcomeUnknown);
            }
        }

        return Map(result);
    }

    /// <summary>Wake dispatch after the accepted reply is written or its write fails.</summary>
    public void AfterReply(IpcResponse response)
    {
        if (response.Outcome == "accepted" || response.Error == IpcProtocol.OutcomeUnknown)
        {
            onAccepted();
        }
    }

    static IpcResponse Map(JobResult result) =>
        result.Error is null ? new IpcResponse(true, Outcome: result.Outcome, Job: result.Job) : new IpcResponse(false, result.Error);
}
