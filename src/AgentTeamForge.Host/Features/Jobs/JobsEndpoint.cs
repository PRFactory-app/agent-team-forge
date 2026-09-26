using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Jobs;

/// <summary>Thin IPC mapping for job_submit/job_get/job_list; all rules live in Business.</summary>
public sealed class JobsEndpoint(AcceptJob accept, GetJob get, ListJobs list, DurabilityCheckpoints checkpoints, Action onAccepted)
{
    public IpcResponse Handle(IpcRequest request)
    {
        switch (request.Op)
        {
            case IpcProtocol.JobSubmit:
                var submitted = accept.Execute(new SubmitJobRequest(request.IdempotencyKey ?? string.Empty, request.Instruction ?? string.Empty, request.Behavior, request.Hold));
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
                return Map(get.Execute(request.JobId ?? string.Empty));
            case IpcProtocol.JobList:
                var listed = list.Execute(new ListJobsRequest(request.Status, request.Limit, request.Cursor));
                return listed.Error is null ? new IpcResponse(true, Outcome: "listed", Page: listed.Page) : new IpcResponse(false, listed.Error);
            default:
                return new IpcResponse(false, IpcProtocol.UnknownOp);
        }
    }

    /// <summary>
    /// Wakes dispatch only after the accepted reply was written (or its write
    /// failed), so the post-commit crash boundary and the reply itself cannot
    /// race a claim of the job they describe.
    /// </summary>
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
