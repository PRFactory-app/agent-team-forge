using System.Globalization;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.External;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Features.Sessions;
using AgentTeamForge.DAL.Features.External;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Jobs;

/// <summary>Thin IPC mapping for the job operations; all rules live in Business.</summary>
public sealed class JobsEndpoint(AcceptJob accept, GetJob get, FollowUpJob followUp, ListJobs list, StopJob stop, DurabilityCheckpoints checkpoints, Action onAccepted,
    WakeStore? wakeStore = null, PruneJob? prune = null, JobLogs? logs = null, JobStore? jobStore = null, LeadSessionStore? sessions = null, ExternalTeam? external = null,
    StopAgent? stopAgent = null, IReadOnlyCollection<string>? configuredBackends = null, TierMap? tierMap = null,
    BackendModelDiscovery? modelDiscovery = null, HerdrPlacement? herdrPlacement = null, ClaudeWakeMailbox? claudeMailbox = null, string? launchMode = null,
    Func<string?, string?, string?, HumanInputRequestResult>? humanInput = null, ExternalMemberStore? externalMembers = null,
    GetJob? connectorGet = null, Func<string, string, AttemptClaim?>? takeNativeClaude = null,
    RemoveWorktree? removeWorktree = null, WorktreeCleanup? worktreeCleanup = null, Func<string?, string?, string, bool?>? agentLive = null, InteractiveRetentionConfiguration? retentionSettings = null, Action<string, string>? releaseNativeTurn = null, AgentTeamForge.Business.Features.Usage.SessionTokenUsage? usage = null,
    StopIdleAgents? stopIdle = null, ArchiveJobs? archive = null)
{
    JobResult ReadJob(IpcRequest request)
    {
        var result = get.Execute(request.JobId ?? string.Empty);
        return result.Error == JobErrors.NotFound && request.IncludeConnector && request.LeadSessionId is null && connectorGet is not null
            ? connectorGet.Execute(request.JobId ?? string.Empty) : result;
    }

    public IpcResponse Handle(IpcRequest request)
    {
        // Relay requests must not acquire WakeRoutingGate: the coordinator holds it while awaiting this receipt.
        if (request.Op == IpcProtocol.ClaudeWakeTake)
        {
            return new IpcResponse(true, ClaudeNotice: claudeMailbox?.Take(request.WakeAddress, request.WakeSecret, request.WakeHome));
        }
        if (request.Op == IpcProtocol.ClaudeWakeComplete)
        {
            return new IpcResponse(claudeMailbox?.Complete(request.NoticeId, request.WakeAddress,
                request.WakeSecret, request.WakeHome, request.NoticePosted) == true);
        }
        if (request.Op is IpcProtocol.ClaudeDeliveryTake or IpcProtocol.ClaudeDeliveryComplete)
        {
            if (sessions is null || jobStore is null || request.LeadSessionId is null || request.Workspace is null
                || request.JobId is null || request.WakeKind != "claude"
                || !AgentTeamForge.Business.Features.Wake.ClaudeChannel.Valid(request.WakeAddress, request.WakeSecret, request.WakeHome))
            {
                return new IpcResponse(false, JobErrors.InvalidRequest);
            }
            var childId = request.Op == IpcProtocol.ClaudeDeliveryTake ? request.JobId
                : jobStore.NativeClaudeAttempt(request.JobId)?.ChildJobId;
            if (childId is null || !sessions.IsManagedChild(request.LeadSessionId, request.Workspace, childId)
                || jobStore.ManagedClaudeChannel(childId) is not { } channel
                || channel.Address != request.WakeAddress || channel.Secret != request.WakeSecret || channel.Host != request.WakeHome)
            {
                return new IpcResponse(false, IpcProtocol.AccessDenied);
            }
            if (request.Op == IpcProtocol.ClaudeDeliveryTake)
            {
                if (takeNativeClaude is null || string.IsNullOrWhiteSpace(request.Text)) { return new IpcResponse(true); }
                var claim = takeNativeClaude(childId, request.Text);
                return new IpcResponse(true, ClaudeDelivery: claim is null ? null
                    : new NativeClaudeOffer(claim.Job.JobId, claim.RunId, claim.Correlation, claim.Job.Instruction));
            }
            if (request.NativeCorrelation is null || request.NativeRunId is null) { return new IpcResponse(false, JobErrors.InvalidRequest); }
            var attempt = jobStore.NativeClaudeAttempt(request.JobId);
            if (attempt is null || attempt.Correlation != request.NativeCorrelation || attempt.State != "posting")
            {
                return new IpcResponse(false, JobErrors.InvalidRequest);
            }
            if (!request.NativeWriteStarted)
            {
                var reverted = jobStore.RevertNativeClaudeAttempt(new RunRef(request.JobId, request.NativeRunId, 1, request.NativeCorrelation));
                if (reverted) { releaseNativeTurn?.Invoke("claude", attempt.SessionId); }
                return new IpcResponse(reverted);
            }
            if (request.NoticePosted) { jobStore.RecordNativeClaudePost(request.JobId, request.NativeCorrelation); }
            return new IpcResponse(true);
        }
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
                IpcProtocol.ExternalSetWake => request.WakeKind == "claude"
                    ? external.SetClaudeWake(request.MemberToken, request.WakeAddress, request.WakeSecret, request.WakeHome)
                    : external.SetWake(request.MemberToken, request.CodexThreadId, request.WakeHome),
                _ => external.Leave(request.MemberToken)
            });
        }
        if (request.Op == IpcProtocol.SessionStart)
        {
            if (sessions is null || !ValidWorkspace(request))
            {
                return new IpcResponse(false, JobErrors.InvalidRequest);
            }
            return new IpcResponse(true, Outcome: "session", Session: sessions.Start(request.Workspace!, request.BindingKey!, request.NativeKind, request.NativeSessionId, request.NativeHome));
        }
        if (request.Op == IpcProtocol.SessionResume)
        {
            if (sessions is null || !ValidWorkspace(request) || request.LeadSessionId is null)
            {
                return new IpcResponse(false, JobErrors.InvalidRequest);
            }
            var (resumed, liveOwner) = sessions.Resume(request.LeadSessionId, request.Workspace!, request.BindingKey!, request.NativeKind, request.NativeSessionId, request.NativeHome, request.Force, request.WakeKey);
            return liveOwner is not null
                ? new IpcResponse(false, JobErrors.SessionOwned, ErrorDetail: $"Session {request.LeadSessionId} is bound to live native session {liveOwner}; adopting it would take over that lead's wakes and results. Pass force=true only if that session is dead.")
                : resumed is null ? new IpcResponse(false, JobErrors.NotFound) : new IpcResponse(true, Outcome: "resumed", Session: resumed);
        }
        if (request.Op == IpcProtocol.SessionInfo)
        {
            if (sessions is null || request.LeadSessionId is null || request.Workspace is null)
            {
                return new IpcResponse(false, JobErrors.InvalidRequest);
            }
            if (request.SessionName is { } sessionName)
            {
                var name = sessionName.Trim();
                if (name.Length > 64 || sessionName.Any(char.IsControl))
                {
                    return new IpcResponse(false, JobErrors.InvalidRequest, ErrorDetail: "Invalid name: at most 64 characters without control characters.");
                }
                if (!sessions.Rename(request.LeadSessionId, request.Workspace, name.Length == 0 ? null : name))
                {
                    return new IpcResponse(false, JobErrors.NotFound);
                }
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
        if (request.Op != IpcProtocol.ExternalOperatorSend && request.LeadSessionId is not null
            && (sessions is null || request.Workspace is null || !sessions.Exists(request.LeadSessionId, request.Workspace)))
        {
            return new IpcResponse(false, JobErrors.InvalidRequest,
                ErrorDetail: "Unknown lead session for this workspace; restart the MCP bridge (session_info) and retry.");
        }
        var addressed = request.Op is IpcProtocol.JobGet or IpcProtocol.JobFollowUp or IpcProtocol.JobStop or IpcProtocol.ExternalLeadRead
            or IpcProtocol.JobOutput or IpcProtocol.JobActivity or IpcProtocol.JobRelease or IpcProtocol.JobStopAgent or IpcProtocol.JobRemoveWorktree;
        if (addressed && request.LeadSessionId is { } caller && request.Workspace is { } workspace && sessions is not null)
        {
            var jobId = request.JobId;
            if (request.Op == IpcProtocol.ExternalLeadRead && jobId is null)
            {
                jobId = sessions.RecoverableJob(caller, workspace, DateTimeOffset.UtcNow);
            }
            if (jobId is not null || request.Op != IpcProtocol.ExternalLeadRead)
            {
                var reach = sessions.ReachJob(jobId ?? string.Empty, caller, workspace, DateTimeOffset.UtcNow);
                if (reach.Error is { } error)
                {
                    var tool = error is "owned_by_live_lead" or "owned_by_previous_session" ? "resume_session" : "list_jobs";
                    return new IpcResponse(false, error, ErrorDetail: error switch
                    {
                        "owned_by_live_lead" => $"Job {jobId} belongs to live lead {reach.LiveOwner}. Explicit resume_session with force=true is required to take over.",
                        "owned_by_previous_session" => "Automatic ownership cannot be established. Use explicit resume_session for the previous session if it is yours.",
                        "expired" => "The job's 30-day reach window has expired. List jobs or submit new work.",
                        _ => "Unknown job. Use list_jobs to find an existing job."
                    })
                    {
                        Recovery = new JobRecovery(tool, new JobRecoveryArguments(null)
                        { SessionId = tool == "resume_session" ? reach.PreviousSessionId : null, Force = error == "owned_by_live_lead" ? true : null }),
                        OwnerLead = reach.LiveOwner,
                        ReachExpiresAt = reach.ExpiresAt
                    };
                }
                if (reach.Session is { } adopted)
                {
                    return Handle(request with { LeadSessionId = adopted.SessionId }) with { Session = adopted };
                }
            }
        }
        switch (request.Op)
        {
            case IpcProtocol.JobCapabilities:
                _ = modelDiscovery?.Warm(configuredBackends ?? []);
                var installed = BackendAvailability.ReadInstalled(configuredBackends ?? []);
                return new IpcResponse(true, Outcome: "capabilities", Backends: configuredBackends ?? [],
                    BackendAvailability: BackendAvailability.Read(configuredBackends ?? [], launchMode), BackendInstalled: installed,
                    BackendSignIn: BackendAvailability.ReadSignIn(installed), LaunchMode: launchMode, ModelOptions: ModelSelection.ConsoleOptions,
                    ModelCatalog: new Dictionary<string, IReadOnlyCollection<string>>
                    {
                        ["codex"] = modelDiscovery?.CachedModels("codex") ?? [],
                        ["pi"] = modelDiscovery?.CachedModels("pi") ?? [],
                        ["cursor"] = modelDiscovery?.CachedModels("cursor") ?? []
                    },
                    Tiers: tierMap?.Settings(), HerdrPlacement: herdrPlacement?.Default, HerdrMode: herdrPlacement is not null);
            case IpcProtocol.HerdrPlacementGet:
                return herdrPlacement is null ? new IpcResponse(false, JobErrors.InvalidRequest)
                    : new IpcResponse(true, Outcome: "herdr_placement", HerdrPlacement: herdrPlacement.Default, HerdrMode: true);
            case IpcProtocol.HerdrPlacementPut:
                if (herdrPlacement is null || request.HerdrPlacement is null) { return new IpcResponse(false, JobErrors.InvalidRequest); }
                try { herdrPlacement.Change(request.HerdrPlacement); return new IpcResponse(true, Outcome: "herdr_placement", HerdrPlacement: herdrPlacement.Default, HerdrMode: true); }
                catch (ArgumentException e) { return new IpcResponse(false, JobErrors.InvalidRequest, ErrorDetail: "Invalid herdr_placement: " + e.Message); }
            case IpcProtocol.RetentionSettingsGet:
            case IpcProtocol.RetentionSettingsPut:
                return retentionSettings?.Handle(request) ?? new IpcResponse(false, JobErrors.InvalidRequest);
            case IpcProtocol.TierSettingsGet:
                _ = modelDiscovery?.Warm(configuredBackends ?? []);
                return tierMap is null ? new IpcResponse(false, JobErrors.InvalidRequest)
                    : new IpcResponse(true, Outcome: "tiers", Tiers: tierMap.Settings(), ModelCatalog:
                        new Dictionary<string, IReadOnlyCollection<string>>
                        {
                            ["codex"] = modelDiscovery?.CachedModels("codex") ?? [],
                            ["pi"] = modelDiscovery?.CachedModels("pi") ?? [],
                            ["cursor"] = modelDiscovery?.CachedModels("cursor") ?? []
                        }, ModelEfforts: tierMap.ModelEfforts());
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
                catch (TierSettingException ex) { return new IpcResponse(false, JobErrors.InvalidRequest, ErrorDetail: ex.Detail); }
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
            case IpcProtocol.ExternalOperatorSend:
                return external is null ? new IpcResponse(false, JobErrors.InvalidRequest)
                    : MapExternal(external.SendFromOperator(request.LeadSessionId, request.Workspace, request.Text, request.IdempotencyKey));
            case IpcProtocol.JobSubmit:
                var (submitWakeKey, submitWakeGeneration) = LeadWake(request);
                return Accepted(accept.Execute(new SubmitJobRequest(request.IdempotencyKey ?? string.Empty, request.Instruction ?? string.Empty, request.Behavior, request.Hold)
                {
                    Backend = request.Backend,
                    TargetAgent = request.TargetAgent,
                    ExpectedOutputs = request.ExpectedOutputs,
                    Model = request.Model,
                    Effort = request.Effort,
                    HerdrPlacement = request.HerdrPlacement,
                    Cwd = request.Cwd,
                    Worktree = request.Worktree,
                    TimeoutSeconds = request.TimeoutSeconds,
                    QueueTtlSeconds = request.QueueTtlSeconds,
                    LeadSessionId = request.LeadSessionId,
                    WakeKey = submitWakeKey,
                    WakeGeneration = submitWakeGeneration,
                }))
                with
                {
                    HerdrMode = herdrPlacement is not null,
                };
            case IpcProtocol.JobFollowUp:
                var (followUpWakeKey, followUpWakeGeneration) = LeadWake(request);
                var followed = Accepted(followUp.Execute(new FollowUpRequest(request.JobId ?? string.Empty, request.Instruction ?? string.Empty, request.IdempotencyKey ?? string.Empty)
                {
                    TimeoutSeconds = request.TimeoutSeconds,
                    QueueTtlSeconds = request.QueueTtlSeconds,
                    LeadSessionId = request.LeadSessionId,
                    Interrupt = request.Interrupt,
                    Defer = request.Defer,
                    ReplaceIfIdle = request.ReplaceIfIdle,
                    Model = request.Model,
                    Effort = request.Effort,
                    WakeKey = followUpWakeKey,
                    WakeGeneration = followUpWakeGeneration,
                }))
                with
                {
                    HerdrMode = herdrPlacement is not null,
                };
                if (followed.Ok) { MarkParentWakeRead(request, request.JobId); }
                return followed;
            case IpcProtocol.JobRelease:
                var released = stop.Release(request.JobId ?? string.Empty);
                if (released.Error is null && released.Job is not null) { MarkWakeRead(request, released.Job.JobId, released.Job.Status, released.Job.Revision); }
                return Map(released);
            case IpcProtocol.JobStop:
                var stopped = stop.Execute(request.JobId ?? string.Empty, request.Interrupt);
                if (stopped.Error is null && stopped.Job is not null) { MarkWakeRead(request, stopped.Job.JobId, stopped.Job.Status, stopped.Job.Revision); }
                return Map(stopped);
            case IpcProtocol.JobStopAgent:
                if (stopAgent is null) { return new IpcResponse(false, JobErrors.BackendUnavailable, ErrorDetail: "stop_agent is not available in this daemon."); }
                var stoppedAgent = stopAgent.Execute(request.JobId ?? string.Empty);
                if (stoppedAgent.Error is null && stoppedAgent.Job is not null) { MarkWakeRead(request, stoppedAgent.Job.JobId, stoppedAgent.Job.Status, stoppedAgent.Job.Revision); }
                return Map(stoppedAgent);
            case IpcProtocol.JobStopIdle:
                if (stopIdle is null || request.LeadSessionId is not null) { return new IpcResponse(false, JobErrors.BackendUnavailable, ErrorDetail: "Stop idle agents is not available here."); }
                if (request.DryRun) { return new IpcResponse(true, Outcome: "dry_run", Counts: stopIdle.ExecuteAsync(true).GetAwaiter().GetResult().ToMap()); }
                // Closing panes can take longer than the console's IPC deadline, so it runs in the background and is polled.
                var began = stopIdle.Start();
                return new IpcResponse(true, Outcome: began ? "started" : "running", Counts: stopIdle.Status().Counts.ToMap());
            case IpcProtocol.JobStopIdleStatus:
                if (stopIdle is null || request.LeadSessionId is not null) { return new IpcResponse(false, JobErrors.BackendUnavailable, ErrorDetail: "Stop idle agents is not available here."); }
                var (isRunning, progress) = stopIdle.Status();
                return new IpcResponse(true, Outcome: isRunning ? "running" : "done", Counts: progress.ToMap());
            case IpcProtocol.JobArchiveFinished:
                if (archive is null || request.LeadSessionId is not null) { return new IpcResponse(false, JobErrors.BackendUnavailable, ErrorDetail: "Clear finished is not available here."); }
                try
                {
                    var archived = archive.Execute(request.DryRun);
                    return new IpcResponse(true, Outcome: request.DryRun ? "dry_run" : "archived", Counts: new BulkCounts(0, 0, 0, archived, request.DryRun ? 0 : archived).ToMap());
                }
                catch (StorageException ex) { return new IpcResponse(false, JobErrors.FromStorage(ex), ErrorDetail: JobErrors.StorageDetail(ex)); }
            case IpcProtocol.JobRemoveWorktree:
                if (removeWorktree is null) { return new IpcResponse(false, JobErrors.BackendUnavailable); }
                var (removal, removalError) = removeWorktree.ExecuteAsync(request.JobId ?? string.Empty, request.Force, request.DryRun, CancellationToken.None).GetAwaiter().GetResult();
                return removal is null ? new IpcResponse(false, removalError)
                    : new IpcResponse(true, Outcome: removal.Outcome, Worktrees: [removal]);
            case IpcProtocol.JobPruneWorktrees:
                if (worktreeCleanup is null || request.Force && request.JobId is null) { return new IpcResponse(false, JobErrors.InvalidRequest); }
                var results = new List<WorktreeCleanupResult>();
                if (request.JobId is not null)
                {
                    var (single, singleError) = worktreeCleanup.RemoveJobAsync(request.JobId, request.Force, request.DryRun, CancellationToken.None).GetAwaiter().GetResult();
                    if (single is null) { return new IpcResponse(false, singleError); }
                    results.Add(single);
                }
                else
                {
                    foreach (var target in worktreeCleanup.ListWorktrees())
                    {
                        results.Add(worktreeCleanup.RemoveAsync(target, request.Force, request.DryRun, auto: false, CancellationToken.None).GetAwaiter().GetResult());
                    }
                }
                return new IpcResponse(true, Outcome: request.DryRun ? "dry_run" : "pruned", Worktrees: results);
            case IpcProtocol.JobGet:
                var found = ReadJob(request);
                if (found.Error is null && found.Job is not null)
                {
                    MarkWakeRead(request, found.Job.JobId, found.Job.Status, found.Job.Revision);
                    MarkParentWakeRead(request, found.Job.ParentJobId);
                }
                var located = WithLocation(found);
                if (!request.IncludeInstruction && located.Job is not null) { located = located with { Job = located.Job with { Instruction = null } }; }
                return Map(located) with { HerdrMode = herdrPlacement is not null };
            case IpcProtocol.JobOutput:
                var outputJob = ReadJob(request);
                if (outputJob.Error is not null)
                {
                    return new IpcResponse(false, outputJob.Error, ErrorDetail: outputJob.Detail);
                }
                if (logs is null) { return new IpcResponse(false, JobErrors.InvalidRequest); }
                if (request.Offset is < 0) { return new IpcResponse(false, JobErrors.InvalidRequest, ErrorDetail: "Invalid offset: must be nonnegative."); }
                if (request.MaxBytes is < 1 or > JobLogs.MaxReadBytes)
                {
                    return new IpcResponse(false, JobErrors.InvalidRequest,
                        ErrorDetail: string.Create(CultureInfo.InvariantCulture, $"Invalid max_bytes: must be an integer from 1 to {JobLogs.MaxReadBytes}."));
                }
                MarkWakeRead(request, outputJob.Job!.JobId, outputJob.Job.Status, outputJob.Job.Revision);
                return new IpcResponse(true, Outcome: "output", Output: logs.Read(request.JobId!, request.Offset ?? 0, request.MaxBytes ?? JobLogs.MaxReadBytes));
            case IpcProtocol.JobActivity:
                var activityJob = ReadJob(request);
                if (activityJob.Error is not null)
                {
                    return new IpcResponse(false, activityJob.Error, ErrorDetail: activityJob.Detail);
                }
                if (logs is null) { return new IpcResponse(false, JobErrors.InvalidRequest); }
                if (request.AfterCursor is < 0) { return new IpcResponse(false, JobErrors.InvalidRequest, ErrorDetail: "Invalid after_cursor: must be nonnegative."); }
                if (request.Limit is < 1 or > JobActivity.MaxPageSize)
                {
                    return new IpcResponse(false, JobErrors.InvalidRequest,
                        ErrorDetail: string.Create(CultureInfo.InvariantCulture, $"Invalid limit: must be an integer from 1 to {JobActivity.MaxPageSize}."));
                }
                return new IpcResponse(true, Outcome: "activity", Activity: logs.ReadActivity(request.JobId!, activityJob.Job!.Backend ?? "", request.AfterCursor ?? 0, request.Limit ?? 20));
            case IpcProtocol.JobList:
                var listed = list.Execute(new ListJobsRequest(request.Status, request.Limit, request.Cursor, request.Backend, request.Since)
                {
                    LeadSessionId = request.LeadSessionId,
                    AllWorkspace = request.AllWorkspace,
                    Workspace = request.Workspace,
                    OrderByActivity = request.OrderByActivity,
                    IncludeConnector = request.IncludeConnector && request.LeadSessionId is null,
                    Unread = request.Unread,
                    ExcludeArchived = request.ExcludeArchived,
                });
                if (listed.Error is not null) { return new IpcResponse(false, listed.Error, ErrorDetail: listed.Detail); }
                foreach (var job in listed.Page!.Jobs) { MarkWakeRead(request, job.JobId, job.Status, job.Revision); }
                return new IpcResponse(true, Outcome: "listed", Page: WithUsage(WithLocations(listed.Page), request.IncludeUsage), LeadTokens: request.IncludeUsage ? LeadUsage(listed.Page) : null,
                    ExternalMembers: request.IncludeConnector ? externalMembers?.ActiveMcpMembers() : null);
            case IpcProtocol.JobPrune:
                if (prune is null) { return new IpcResponse(false, JobErrors.InvalidRequest); }
                if (request.OlderThanDays is not (>= 1 and <= 36500))
                {
                    return new IpcResponse(false, JobErrors.InvalidRequest, ErrorDetail: "Invalid older_than_days: must be an integer from 1 to 36500.");
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
            case IpcProtocol.HumanInputRequest:
                {
                    // Authenticated by the managed child's private member token, never by a caller-supplied job ID.
                    var asked = humanInput?.Invoke(request.MemberToken, request.Text, request.IdempotencyKey)
                        ?? new HumanInputRequestResult(null, IpcProtocol.UnknownOp);
                    return asked.Error is { } error ? new IpcResponse(false, error, ErrorDetail: asked.Detail)
                        : new IpcResponse(true, Outcome: asked.Wait!.QuestionId, Instruction: asked.Instruction);
                }
            default:
                return new IpcResponse(false, IpcProtocol.UnknownOp);
        }
    }

    // The lead session's current binding wins over a bridge's cached key, so a stale bridge never drops a job's wake.
    (string? Key, long? Generation) LeadWake(IpcRequest request) =>
        request.LeadSessionId is not null && wakeStore?.Status(request.LeadSessionId) is { Registered: true, Key: { } key, Generation: { } generation }
            ? (key, generation) : (request.WakeKey, request.WakeGeneration);

    void MarkWakeRead(IpcRequest request, string jobId, string observedStatus, long revision)
    {
        if (wakeStore is null) { return; }
        if (request.LeadSessionId is { } leadSessionId) { wakeStore.MarkReadForLead(jobId, observedStatus, revision, leadSessionId); }
        else if (LeadWake(request) is { Key: { } key, Generation: long generation }) { wakeStore.MarkRead(jobId, observedStatus, key, generation, revision); }
    }

    // A follow-up chain consumes its parent: acknowledging the child also acknowledges a finished parent.
    void MarkParentWakeRead(IpcRequest request, string? parentJobId)
    {
        if (parentJobId is not null && jobStore is not null
            && (request.LeadSessionId is null || jobStore.LeadCanAccess(parentJobId, request.LeadSessionId, null))
            && jobStore.GetJob(parentJobId) is { } parent)
        {
            MarkWakeRead(request, parent.JobId, parent.Status, parent.Revision);
        }
    }

    static bool ValidWorkspace(IpcRequest request) => request.Workspace is { Length: > 0 and <= 4096 } workspace
        && Path.IsPathFullyQualified(workspace) && request.BindingKey is { Length: > 0 and <= 4096 };

    JobResult WithLocation(JobResult result)
    {
        if (result.Job is { } view) { result = result with { Job = view with { AgentLive = agentLive?.Invoke(view.Backend, view.SessionId, view.Status) } }; }
        if (herdrPlacement is null || result.Job is not { HerdrPlacement: not null } job) { return result; }
        var location = Location(job.JobId, job.ParentJobId);
        return location is null ? result : result with { Job = job with { HerdrSession = location.Value.Session, HerdrTab = location.Value.TabId, HerdrTabLabel = location.Value.TabLabel } };
    }

    // A retained follow-up reuses its parent's tab, so its record carries the parent's job id.
    (string Session, string? TabId, string? TabLabel)? Location(string jobId, string? parentJobId) =>
        HerdrOwnedSessions.Location(herdrPlacement!.StatePath, jobId)
        ?? (parentJobId is null ? null : HerdrOwnedSessions.Location(herdrPlacement!.StatePath, parentJobId));

    JobListPage WithUsage(JobListPage page, bool include) => !include || usage is null ? page : page with
    {
        Jobs = [.. page.Jobs.Select(j => j with { SessionTokens = j.SessionId is null ? null : usage.Read(j.Backend ?? "", j.SessionId, null, j.Cwd) })]
    };

    Dictionary<string, AgentTeamForge.Business.Features.Usage.TokenUsage?> LeadUsage(JobListPage page)
    {
        var ids = page.Jobs.Select(j => j.LeadSessionId).OfType<string>().Distinct().ToArray();
        var result = ids.ToDictionary(id => id, _ => (AgentTeamForge.Business.Features.Usage.TokenUsage?)null);
        if (sessions is not null && usage is not null)
        {
            foreach (var binding in sessions.NativeBindings(ids))
            {
                result[binding.SessionId] = usage.Read(binding.Kind, binding.NativeId, binding.Home);
            }
        }
        return result;
    }
    JobListPage WithLocations(JobListPage page)
    {

        var live = new Dictionary<(string? Backend, string? Session), bool?>();
        return page with
        {
            Jobs = [.. page.Jobs.Select(job =>
            {
                var key = (job.Backend, job.SessionId);
                if (!live.TryGetValue(key, out var observed))
                {
                    observed = agentLive?.Invoke(job.Backend, job.SessionId, job.Status);
                    live[key] = observed;
                }
                job = job with { AgentLive = observed };
                if (herdrPlacement is null || job.HerdrPlacement is null) { return job; }
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
        result.Error is null ? new IpcResponse(true, Outcome: result.Outcome, Job: result.Job) : new IpcResponse(false, result.Error, ErrorDetail: result.Detail) { FencingJobId = result.FencingJobId, Recovery = result.Recovery };

    static IpcResponse MapExternal(ExternalResult result) => new(result.Ok, result.Error,
        result.Ok ? "ok" : null, WakeGeneration: result.WakeGeneration, Ticket: result.Ticket,
        Member: result.Member, Inbox: result.Inbox, AlreadyLeft: result.AlreadyLeft, LeftName: result.Name,
        ErrorDetail: result.ErrorDetail);
}
