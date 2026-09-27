using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.External;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Features.Sessions;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Jobs;

/// <summary>Thin IPC mapping for the job operations; all rules live in Business.</summary>
public sealed class JobsEndpoint(AcceptJob accept, GetJob get, FollowUpJob followUp, ListJobs list, StopJob stop, DurabilityCheckpoints checkpoints, Action onAccepted,
    WakeStore? wakeStore = null, PruneJob? prune = null, JobLogs? logs = null, JobStore? jobStore = null, LeadSessionStore? sessions = null, ExternalTeam? external = null,
    StopAgent? stopAgent = null, IReadOnlyCollection<string>? configuredBackends = null, TierMap? tierMap = null,
    BackendModelDiscovery? modelDiscovery = null, HerdrPlacement? herdrPlacement = null)
{
    public IpcResponse Handle(IpcRequest request)
    {
        if (request.Op is IpcProtocol.ExternalJoin or IpcProtocol.ExternalSend or IpcProtocol.ExternalRead or IpcProtocol.ExternalSetWake or IpcProtocol.ExternalLeave)
        {
            using var routing = request.Op == IpcProtocol.ExternalSetWake ? WakeRoutingGate.Enter() : null;
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
            return info is null ? new IpcResponse(false, JobErrors.NotFound)
                : new IpcResponse(true, Outcome: "session", Session: info, Tiers: tierMap?.Settings());
        }
        if (request.Op == IpcProtocol.SessionBindWake)
        {
            using var routing = WakeRoutingGate.Enter();
            if (sessions is null || wakeStore is null || request.LeadSessionId is null || request.Workspace is null || request.WakeKey is null || request.WakeGeneration is null
                || !sessions.Exists(request.LeadSessionId, request.Workspace))
            {
                return new IpcResponse(false, JobErrors.InvalidRequest);
            }
            if (!wakeStore.IsCurrent(new WakeRegistration(request.WakeKey, request.WakeGeneration.Value, "", "", "", "")))
            {
                return new IpcResponse(false, JobErrors.InvalidRequest);
            }
            var previous = wakeStore.Status(request.LeadSessionId);
            if (previous is { Registered: true, Key: not null, Generation: not null } && previous.Key != request.WakeKey)
            {
                wakeStore.ClearLead(request.LeadSessionId, previous.Key, previous.Generation.Value);
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
            case IpcProtocol.JobCapabilities:
                return new IpcResponse(true, Outcome: "capabilities", Backends: configuredBackends ?? [], ModelOptions: ModelSelection.ConsoleOptions,
                    Tiers: tierMap?.Settings(), HerdrPlacement: herdrPlacement?.Default, HerdrMode: herdrPlacement is not null);
            case IpcProtocol.HerdrPlacementGet:
                return herdrPlacement is null ? new IpcResponse(false, JobErrors.InvalidRequest)
                    : new IpcResponse(true, Outcome: "herdr_placement", HerdrPlacement: herdrPlacement.Default, HerdrMode: true);
            case IpcProtocol.HerdrPlacementPut:
                if (herdrPlacement is null || request.HerdrPlacement is null) { return new IpcResponse(false, JobErrors.InvalidRequest); }
                try { herdrPlacement.Change(request.HerdrPlacement); return new IpcResponse(true, Outcome: "herdr_placement", HerdrPlacement: herdrPlacement.Default, HerdrMode: true); }
                catch (ArgumentException e) { return new IpcResponse(false, e.Message); }
            case IpcProtocol.TierSettingsGet:
                return tierMap is null ? new IpcResponse(false, JobErrors.InvalidRequest)
                    : new IpcResponse(true, Outcome: "tiers", Tiers: tierMap.Settings(), ModelCatalog:
                        new Dictionary<string, IReadOnlyCollection<string>>
                        {
                            ["codex"] = modelDiscovery?.CachedModels("codex") ?? [],
                            ["pi"] = modelDiscovery?.CachedModels("pi") ?? []
                        });
            case IpcProtocol.TierSettingsPut:
                if (tierMap is null)
                {
                    return new IpcResponse(false, JobErrors.InvalidRequest);
                }

                try
                {
                    tierMap.Change(request.Backend, request.Tier, request.Model, request.Effort, request.ResetAllTiers);
                    return new IpcResponse(true, Outcome: "tiers", Tiers: tierMap.Settings());
                }
                catch (ArgumentException ex) { return new IpcResponse(false, ex.Message); }
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
                    TargetAgent = request.TargetAgent,
                    Model = request.Model,
                    Effort = request.Effort,
                    HerdrPlacement = request.HerdrPlacement,
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
                    Model = request.Model,
                    Effort = request.Effort,
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
                return Map(WithLocation(found)) with { HerdrMode = herdrPlacement is not null };
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
                return listed.Error is null ? new IpcResponse(true, Outcome: "listed", Page: WithLocations(listed.Page!)) : new IpcResponse(false, listed.Error);
            case IpcProtocol.JobPrune:
                if (prune is null || request.OlderThanDays is not (>= 1 and <= 36500))
                {
                    return new IpcResponse(false, JobErrors.InvalidRequest);
                }
                return new IpcResponse(true, Outcome: request.DryRun ? "dry_run" : "pruned",
                    PrunedJobs: prune.Execute(request.OlderThanDays.Value, request.DryRun));
            case IpcProtocol.WakeRegister:
                using (WakeRoutingGate.Enter())
                {
                    if (wakeStore is null || string.IsNullOrWhiteSpace(request.WakeKey) || request.WakeKey.Length > 256
                        || request.WakeKind is not ("claude" or "codex" or "pi") || string.IsNullOrWhiteSpace(request.WakeAddress)
                        || request.WakeAddress.Length > 4096 || (request.WakeSecret?.Length ?? 0) > 4096
                        || (request.WakeHome?.Length ?? 0) > 4096)
                    {
                        return new IpcResponse(false, JobErrors.InvalidRequest);
                    }
                    // Only a Codex thread ID is self-reported; Claude sockets and Pi hosts come from the host process.
                    if (request.JobId is not null && request.WakeKind == "codex" && (jobStore?.GetJob(request.JobId) is not { Backend: "codex" } spawned
                        || spawned.SessionId != request.WakeAddress
                        || sessions is null || request.LeadSessionId is null || request.Workspace is null
                        || !sessions.IsManagedChild(request.LeadSessionId, request.Workspace, request.JobId)))
                    {
                        return new IpcResponse(false, JobErrors.InvalidRequest);
                    }
                    if (request.LeadSessionId is not null && (sessions is null || request.Workspace is null
                        || !sessions.Exists(request.LeadSessionId, request.Workspace)))
                    {
                        return new IpcResponse(false, JobErrors.InvalidRequest);
                    }
                    var previous = request.LeadSessionId is null ? null : wakeStore.Status(request.LeadSessionId);
                    if (previous is { Registered: true, Key: not null, Generation: not null } && previous.Key != request.WakeKey)
                    {
                        wakeStore.ClearLead(request.LeadSessionId!, previous.Key, previous.Generation.Value);
                    }
                    var registration = wakeStore.Register(request.WakeKey, request.WakeKind, request.WakeAddress,
                        request.WakeSecret ?? string.Empty, request.WakeHome ?? string.Empty);
                    if (request.LeadSessionId is not null) { sessions!.BindWake(request.LeadSessionId, registration.Key, registration.Generation); }
                    return new IpcResponse(true, Outcome: "registered", WakeGeneration: registration.Generation);
                }
            case IpcProtocol.WakeClear:
                using (WakeRoutingGate.Enter())
                {
                    if (wakeStore is null || sessions is null || request.LeadSessionId is null || request.Workspace is null
                        || !sessions.Exists(request.LeadSessionId, request.Workspace)) { return new IpcResponse(false, JobErrors.InvalidRequest); }
                    var current = wakeStore.Status(request.LeadSessionId);
                    if (current is { Registered: true, Key: not null, Generation: not null })
                    {
                        wakeStore.ClearLead(request.LeadSessionId, current.Key, current.Generation.Value);
                    }
                    return new IpcResponse(true, Outcome: "cleared", WakeStatus: wakeStore.Status(request.LeadSessionId));
                }
            case IpcProtocol.WakeStatus:
                if (wakeStore is null || sessions is null || request.LeadSessionId is null || request.Workspace is null
                    || !sessions.Exists(request.LeadSessionId, request.Workspace)) { return new IpcResponse(false, JobErrors.InvalidRequest); }
                return new IpcResponse(true, Outcome: "wake_status", WakeStatus: wakeStore.Status(request.LeadSessionId));
            default:
                return new IpcResponse(false, IpcProtocol.UnknownOp);
        }
    }

    static bool ValidWorkspace(IpcRequest request) => request.Workspace is { Length: > 0 and <= 4096 } workspace
        && Path.IsPathFullyQualified(workspace) && request.BindingKey is { Length: > 0 and <= 4096 };

    JobResult WithLocation(JobResult result)
    {
        if (herdrPlacement is null || result.Job is not { HerdrPlacement: not null } job) { return result; }
        var location = Location(job.JobId, job.ParentJobId);
        return location is null ? result : result with { Job = job with { HerdrSession = location.Value.Session, HerdrTab = location.Value.TabId, HerdrTabLabel = location.Value.TabLabel } };
    }

    // A retained follow-up reuses its parent's tab, so its record carries the parent's job id.
    (string Session, string? TabId, string? TabLabel)? Location(string jobId, string? parentJobId) =>
        HerdrOwnedSessions.Location(herdrPlacement!.StatePath, jobId)
        ?? (parentJobId is null ? null : HerdrOwnedSessions.Location(herdrPlacement!.StatePath, parentJobId));

    JobListPage WithLocations(JobListPage page)
    {
        if (herdrPlacement is null) { return page; }
        return page with
        {
            Jobs = [.. page.Jobs.Select(job =>
            {
                if (job.HerdrPlacement is null) { return job; }
                var location = Location(job.JobId, job.ParentJobId);
                return location is null ? job : job with { HerdrSession = location.Value.Session, HerdrTab = location.Value.TabId, HerdrTabLabel = location.Value.TabLabel };
            })]
        };
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

    static IpcResponse MapExternal(ExternalResult result) => new(result.Ok, result.Error,
        result.Ok ? "ok" : null, WakeGeneration: result.WakeGeneration, Ticket: result.Ticket,
        Member: result.Member, Inbox: result.Inbox, AlreadyLeft: result.AlreadyLeft, LeftName: result.Name,
        ErrorDetail: result.ErrorDetail);
}
