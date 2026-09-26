using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Jobs;

/// <summary>Thin IPC mapping for the job operations; all rules live in Business.</summary>
public sealed class JobsEndpoint(AcceptJob accept, GetJob get, FollowUpJob followUp, StopJob stop, DurabilityCheckpoints checkpoints)
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
                }));
            case IpcProtocol.JobFollowUp:
                return Accepted(followUp.Execute(new FollowUpRequest(request.JobId ?? string.Empty, request.Instruction ?? string.Empty, request.IdempotencyKey ?? string.Empty)));
            case IpcProtocol.JobStop:
                return Map(stop.Execute(request.JobId ?? string.Empty));
            case IpcProtocol.JobGet:
                return Map(get.Execute(request.JobId ?? string.Empty));
            case IpcProtocol.JobList:
                return Map(get.List());
            default:
                return new IpcResponse(false, IpcProtocol.UnknownOp);
        }
    }

    IpcResponse Accepted(JobResult result)
    {
        if (result.Outcome == "accepted")
        {
            checkpoints.Hit(DurabilityCheckpoints.AcceptAfterCommit);
        }

        return Map(result);
    }

    static IpcResponse Map(JobResult result) =>
        result.Error is null ? new IpcResponse(true, Outcome: result.Outcome, Job: result.Job, Jobs: result.Jobs) : new IpcResponse(false, result.Error);
}
