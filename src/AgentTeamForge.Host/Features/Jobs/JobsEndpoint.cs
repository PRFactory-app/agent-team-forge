using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Jobs;

/// <summary>Thin IPC mapping for job_submit/job_get; all rules live in Business.</summary>
public sealed class JobsEndpoint(AcceptJob accept, GetJob get, DurabilityCheckpoints checkpoints)
{
    public IpcResponse Handle(IpcRequest request)
    {
        switch (request.Op)
        {
            case IpcProtocol.JobSubmit:
                var submitted = accept.Execute(new SubmitJobRequest(request.IdempotencyKey ?? string.Empty, request.Instruction ?? string.Empty, request.Behavior, request.Hold));
                if (submitted.Outcome == "accepted")
                {
                    checkpoints.Hit(DurabilityCheckpoints.AcceptAfterCommit);
                }

                return Map(submitted);
            case IpcProtocol.JobGet:
                return Map(get.Execute(request.JobId ?? string.Empty));
            default:
                return new IpcResponse(false, IpcProtocol.UnknownOp);
        }
    }

    static IpcResponse Map(JobResult result) =>
        result.Error is null ? new IpcResponse(true, Outcome: result.Outcome, Job: result.Job) : new IpcResponse(false, result.Error);
}
