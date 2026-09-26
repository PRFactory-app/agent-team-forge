using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Jobs;

/// <summary>Thin IPC mapping for the job operations; all rules live in Business.</summary>
public sealed class JobsEndpoint(AcceptJob accept, GetJob get, FollowUpJob followUp, ListJobs list, DurabilityCheckpoints checkpoints)
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
            case IpcProtocol.JobGet:
                return Map(get.Execute(request.JobId ?? string.Empty));
            case IpcProtocol.JobList:
                var listed = list.Execute(new ListJobsRequest(request.Status, request.Limit, request.Cursor));
                return listed.Error is null ? new IpcResponse(true, Outcome: "listed", Page: listed.Page) : new IpcResponse(false, listed.Error);
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

    static IpcResponse Map(JobResult result) =>
        result.Error is null ? new IpcResponse(true, Outcome: result.Outcome, Job: result.Job) : new IpcResponse(false, result.Error);
}
