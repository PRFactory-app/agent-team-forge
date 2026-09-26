using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Jobs;

/// <summary>Thin IPC mapping for job_submit/job_get/job_list; all rules live in Business.</summary>
public sealed class JobsEndpoint(AcceptJob accept, GetJob get, ListJobs list, DurabilityCheckpoints checkpoints, WakeStore? wakeStore = null)
{
    public IpcResponse Handle(IpcRequest request)
    {
        switch (request.Op)
        {
            case IpcProtocol.JobSubmit:
                var submitted = accept.Execute(new SubmitJobRequest(request.IdempotencyKey ?? string.Empty, request.Instruction ?? string.Empty, request.Behavior, request.Hold,
                    request.WakeKey, request.WakeGeneration));
                if (submitted.Outcome == "accepted")
                {
                    try
                    {
                        checkpoints.Hit(DurabilityCheckpoints.AcceptAfterCommit);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Acceptance is committed but no reply can be produced: never
                        // report "not accepted". The caller recovers by same-key retry.
                        return new IpcResponse(false, IpcProtocol.OutcomeUnknown);
                    }
                }

                return Map(submitted);
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

    static IpcResponse Map(JobResult result) =>
        result.Error is null ? new IpcResponse(true, Outcome: result.Outcome, Job: result.Job) : new IpcResponse(false, result.Error);
}
