using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.External;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Features.Sessions;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Jobs;

/// <summary>Thin IPC mapping for the job operations; all rules live in Business.</summary>
public sealed class JobsEndpoint(AcceptJob accept, GetJob get, FollowUpJob followUp, ListJobs list, StopJob stop, DurabilityCheckpoints checkpoints, Action onAccepted,
    WakeStore? wakeStore = null, PruneJob? prune = null, JobLogs? logs = null, JobStore? jobStore = null, LeadSessionStore? sessions = null, ExternalTeam? external = null,
    StopAgent? stopAgent = null)
{
    public IpcResponse Handle(IpcRequest request)
    {
        if (request.Op is IpcProtocol.ExternalJoin or IpcProtocol.ExternalSend or IpcProtocol.ExternalRead or IpcProtocol.ExternalSetWake or IpcProtocol.ExternalLeave)
        {
            if (external is null)
            {
                return new IpcResponse(false, JobErrors.InvalidRequest);
            }

            return MapExternal(request.Op switch
            {
                IpcProtocol.ExternalJoin => external.Join(request.LeadSessionId, request.TicketToken),
                IpcProtocol.ExternalSend => external.Send(request.MemberToken, request.Text),
                IpcProtocol.ExternalRead => external.Read(request.MemberToken, request.SinceSeq, request.Limit, request.FromAgent, request.Full, request.MaxChars),
                IpcProtocol.ExternalSetWake => external.SetWake(request.MemberToken, request.CodexThreadId, request.WakeHome),
                _ => external.Leave(request.MemberToken)
            });
        }
        if (request.Op == IpcProtocol.SessionStart)
        {
            if (sessions is null || !ValidWorkspace(request))
            {
                return new IpcResponse(false, JobErrors.InvalidRequest);
            }
            return new IpcResponse(true, Outcome: "session", Session: sessions.Start(request.Workspace!, request.BindingKey!));
        }
        if (request.Op == IpcProtocol.SessionResume)
        {
            if (sessions is null || !ValidWorkspace(request) || request.LeadSessionId is null)
            {
                return new IpcResponse(false, JobErrors.InvalidRequest);
            }
            var resumed = sessions.Resume(request.LeadSessionId, request.Workspace!, request.BindingKey!);
            return resumed is null ? new IpcResponse(false, JobErrors.NotFound) : new IpcResponse(true, Outcome: "resumed", Session: resumed);
        }
        if (request.Op == IpcProtocol.SessionInfo)
        {
            if (sessions is null || request.LeadSessionId is null || request.Workspace is null)
            {
                return new IpcResponse(false, JobErrors.InvalidRequest);
            }
            var info = sessions.Info(request.LeadSessionId, request.Workspace);
            return info is null ? new IpcResponse(false, JobErrors.NotFound) : new IpcResponse(true, Outcome: "session", Session: info);
        }
        if (request.Op == IpcProtocol.SessionBindWake)
        {
            if (sessions is null || request.LeadSessionId is null || request.Workspace is null || request.WakeKey is null || request.WakeGeneration is null
                || !sessions.Exists(request.LeadSessionId, request.Workspace))
            {
                return new IpcResponse(false, JobErrors.InvalidRequest);
            }
            sessions.BindWake(request.LeadSessionId, request.WakeKey, request.WakeGeneration.Value);
            return new IpcResponse(true, Outcome: "bound");
        }
        if (request.Op == IpcProtocol.SessionClose)
        {
            if (sessions is null || request.LeadSessionId is null || request.Workspace is null)
            {
                return new IpcResponse(false, JobErrors.InvalidRequest);
            }

            return sessions.Close(request.LeadSessionId, request.Workspace)
                ? new IpcResponse(true, Outcome: "closed") : new IpcResponse(false, JobErrors.NotFound);
        }
        if (request.LeadSessionId is not null && (sessions is null || request.Workspace is null || !sessions.Exists(request.LeadSessionId, request.Workspace)))
        {
            return new IpcResponse(false, JobErrors.InvalidRequest);
        }
        // Reads reach any job in the lead's workspace; stop and follow-up only its own.
        if (request.LeadSessionId is not null && request.Op is IpcProtocol.JobGet or IpcProtocol.JobOutput or IpcProtocol.JobActivity or IpcProtocol.JobStop or IpcProtocol.JobStopAgent or IpcProtocol.JobFollowUp
            && (jobStore is null || request.JobId is null
                || !jobStore.LeadCanAccess(request.JobId, request.LeadSessionId, request.Op is IpcProtocol.JobGet or IpcProtocol.JobOutput or IpcProtocol.JobActivity ? request.Workspace : null)))
        {
            return new IpcResponse(false, JobErrors.NotFound);
        }
        switch (request.Op)
        {
            case IpcProtocol.ExternalTicket:
                return external is null ? new IpcResponse(false, JobErrors.InvalidRequest)
                    : MapExternal(external.CreateTicket(request.LeadSessionId, request.Workspace, request.MemberName, request.Note));
            case IpcProtocol.ExternalLeadSend:
                return external is null ? new IpcResponse(false, JobErrors.InvalidRequest)
                    : MapExternal(external.SendFromLead(request.LeadSessionId, request.Workspace, request.MemberName, request.Text));
            case IpcProtocol.ExternalLeadRead:
                return external is null ? new IpcResponse(false, JobErrors.InvalidRequest)
                    : MapExternal(external.ReadLead(request.LeadSessionId, request.Workspace, request.SinceSeq, request.Limit,
                        request.FromAgent, request.Full, request.MaxChars));
            case IpcProtocol.JobSubmit:
                return Accepted(accept.Execute(new SubmitJobRequest(request.IdempotencyKey ?? string.Empty, request.Instruction ?? string.Empty, request.Behavior, request.Hold)
                {
                    Backend = request.Backend,
                    Cwd = request.Cwd,
                    Worktree = request.Worktree,
                    TimeoutSeconds = request.TimeoutSeconds,
                    QueueTtlSeconds = request.QueueTtlSeconds,
                    LeadSessionId = request.LeadSessionId,
                    WakeKey = request.WakeKey,
                    WakeGeneration = request.WakeGeneration,
                }));
            case IpcProtocol.JobFollowUp:
                return Accepted(followUp.Execute(new FollowUpRequest(request.JobId ?? string.Empty, request.Instruction ?? string.Empty, request.IdempotencyKey ?? string.Empty)
                {
                    TimeoutSeconds = request.TimeoutSeconds,
                    QueueTtlSeconds = request.QueueTtlSeconds,
                    LeadSessionId = request.LeadSessionId,
                    Interrupt = request.Interrupt,
                    WakeKey = request.WakeKey,
                    WakeGeneration = request.WakeGeneration,
                }));
            case IpcProtocol.JobStop:
                return Map(stop.Execute(request.JobId ?? string.Empty));
            case IpcProtocol.JobStopAgent:
                return stopAgent is null ? new IpcResponse(false, JobErrors.BackendUnavailable) : Map(stopAgent.Execute(request.JobId ?? string.Empty));
            case IpcProtocol.JobGet:
                var found = get.Execute(request.JobId ?? string.Empty);
                if (found.Error is null && request.WakeKey is not null && request.WakeGeneration is long generation && wakeStore is not null)
                {
                    wakeStore.MarkRead(request.JobId!, request.WakeKey, generation);
                }
                return Map(found);
            case IpcProtocol.JobOutput:
                var outputJob = get.Execute(request.JobId ?? string.Empty);
                if (outputJob.Error is not null)
                {
                    return new IpcResponse(false, outputJob.Error);
                }
                if (logs is null || request.Offset is < 0 || request.MaxBytes is < 1 or > JobLogs.MaxReadBytes)
                {
                    return new IpcResponse(false, JobErrors.InvalidRequest);
                }
                return new IpcResponse(true, Outcome: "output", Output: logs.Read(request.JobId!, request.Offset ?? 0, request.MaxBytes ?? JobLogs.MaxReadBytes));
            case IpcProtocol.JobActivity:
                var activityJob = get.Execute(request.JobId ?? string.Empty);
                if (activityJob.Error is not null)
                {
                    return new IpcResponse(false, activityJob.Error);
                }
                if (logs is null || request.AfterCursor is < 0 || request.Limit is < 1 or > JobActivity.MaxPageSize)
                {
                    return new IpcResponse(false, JobErrors.InvalidRequest);
                }
                return new IpcResponse(true, Outcome: "activity", Activity: logs.ReadActivity(request.JobId!, activityJob.Job!.Backend ?? "", request.AfterCursor ?? 0, request.Limit ?? 20));
            case IpcProtocol.JobList:
                var listed = list.Execute(new ListJobsRequest(request.Status, request.Limit, request.Cursor, request.Backend, request.Since)
                {
                    LeadSessionId = request.LeadSessionId,
                    AllWorkspace = request.AllWorkspace,
                    Workspace = request.Workspace,
                    OrderByActivity = request.OrderByActivity,
                });
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

    static bool ValidWorkspace(IpcRequest request) => request.Workspace is { Length: > 0 and <= 4096 } workspace
        && Path.IsPathFullyQualified(workspace) && request.BindingKey is { Length: > 0 and <= 4096 };

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

    static IpcResponse MapExternal(ExternalResult result) => new(result.Ok, result.Error,
        result.Ok ? "ok" : null, WakeGeneration: result.WakeGeneration, Ticket: result.Ticket,
        Member: result.Member, Inbox: result.Inbox, AlreadyLeft: result.AlreadyLeft, LeftName: result.Name);
}
