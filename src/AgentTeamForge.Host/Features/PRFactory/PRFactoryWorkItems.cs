using System.Text.Json;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Jobs.Publication;
using AgentTeamForge.Business.Features.External;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Host.Features.PRFactory;

/// <summary>Polls claims and reconciles server ownership before dispatch or publication.</summary>
public sealed partial class PRFactoryWorkItems(
    string server, IReadOnlyList<RepositoryMapping> repositories, PRFactoryTeamStore teams,
    PRFactoryClient client, Func<SubmitJobRequest, JobResult> submit, Func<string, JobRecord?> getJob,
    Action onAccepted, Func<string, string?>? leadSessionFor = null, Action<string>? log = null,
    ExternalTeam? externalTeam = null, Func<string, JobResult>? stopJob = null,
    Func<FollowUpRequest, JobResult>? followUp = null, JobLogs? jobLogs = null,
    PRFactoryAuthority? authority = null, PRFactoryWorkspace? workspaces = null, string? workspaceRoot = null,
    AccountAdmission? accounts = null, int maxAcceptedTeams = 10, PRFactoryPublicationStore? publications = null,
    PRFactoryInteraction? interaction = null, HumanWaitStore? humanWaits = null,
    bool allowRepoLess = false, PRFactoryHandoverStore? handovers = null,
    PRFactoryRepositorySet? repositorySets = null, Action<PRFactoryServerLimit?>? onLimit = null,
    PRFactoryPullRequests? pullRequests = null)
{
    // Parked turns share one account binding per backend until configured accounts exist.
    public const string DefaultAccount = "default";

    // Reserved lead turn for the post-integration finalization; command turns continue above it.
    const int FinalizeTurn = 1000;

    public async Task TickAsync(Guid? machineId, CancellationToken ct)
    {
        if (authority is not null)
        {
            // Unfinished stops survive terminal server dispositions and restarts.
            await authority.RetryStopsAsync(ct);
        }
        foreach (var pending in teams.Pending(server))
        {
            await IsolateAsync(pending.WorkItemId, () => AdvanceAsync(pending, ct), ct);
        }
        if (handovers is not null && workspaces is not null)
        {
            foreach (var key in handovers.ReleasedKeys(server))
            {
                // A removed lead means an earlier cleanup ran; anything left in the root is kept for inspection.
                if (workspaces.Get(key) is not { } released || !Directory.Exists(released.LeadPath)) { continue; }
                var idText = key[(server.Length + 1)..];
                if (!Guid.TryParse(idText, out var id) || teams.Get(server, id) is not { State: not "claimed" }) { continue; }
                if (CleanupRetryAfter.TryGetValue(key, out var next) && next > DateTimeOffset.UtcNow) { continue; }
                await IsolateAsync(id, async () =>
                {
                    var coordinator = new PRFactoryHandover(client, handovers, (_, _, _) => Task.FromResult(false));
                    try
                    {
                        await coordinator.CleanupReleasedAsync(released,
                            () => teams.MemberJobs(server, id).All(job => getJob(job)?.Status is not
                                (JobStatus.Queued or JobStatus.Running or JobStatus.NeedsReconciliation))
                                && teams.ExternalMembers(server, id).All(member => member.Closed), TimeSpan.Zero);
                    }
                    catch (InvalidOperationException ex)
                    {
                        // A deliberate refusal is stable; retry hourly instead of every tick.
                        CleanupRetryAfter[key] = DateTimeOffset.UtcNow.AddHours(1);
                        log?.Invoke($"PRFactory work item {id:D} cleanup refused: {ex.Message}");
                    }
                }, ct);
            }
        }
        if (authority?.IntakeBlocked == true)
        {
            return;
        }

        // Accepted-but-unfinished teams are bounded separately from running processes; a full
        // backlog only stops polling, never fails already accepted work.
        var free = maxAcceptedTeams - teams.AdmissionCount(server);
        if (free <= 0)
        {
            var parked = teams.ParkedCount(server);
            log?.Invoke($"PRFactory intake paused: {maxAcceptedTeams} accepted-team slots occupied"
                + (parked > 0 ? $"; {parked} account-parked team(s) retain admission capacity" : ""));
            return;
        }
        var offered = new List<PRFactoryWorkItem>();
        // Only a poll made in this tick may report the cap; a limit left over from an earlier tick never suppresses polling.
        var capped = false;
        if (repositories.Count > 0)
        {
            offered.AddRange(await client.PollAsync(repositories.Select(r => r.Id), machineId, ct, Math.Min(free, 10)));
            capped = NoteLimit();
        }
        if (allowRepoLess && offered.Count < free && !capped)
        {
            offered.AddRange((await client.PollAsync([], machineId, ct, Math.Min(free - offered.Count, 10)))
                .Where(i => i.RepositoryId is null));
            capped = NoteLimit();
        }
        if (capped)
        {
            return;
        }
        foreach (var item in offered)
        {
            if (item.Type == nameof(PRFactoryWorkItemType.PullRequestCreate))
            {
                // Worker-native: no team, account or admission slot. A lost completion is retried after lease expiry and is idempotent.
                if (item.Id == Guid.Empty || pullRequests is null) { continue; }
                var (claimedPr, prConflict) = await client.ClaimAsync(item.Id, machineId, ct);
                if (prConflict)
                {
                    log?.Invoke($"PRFactory claim of {item.Id:D} refused (409); stopping claims this tick");
                    break;
                }
                if (claimedPr is { } pr && pr.Id == item.Id)
                {
                    await IsolateAsync(item.Id, () => pullRequests.HandleAsync(pr, ct), ct);
                }
                continue;
            }
            if (item.Id == Guid.Empty || teams.Get(server, item.Id) is not null
                || item.RepositoryId is null && !allowRepoLess)
            {
                continue;
            }
            if (accounts is not null && BlockedBackend(item) is { } blocked)
            {
                log?.Invoke($"PRFactory work item {item.Id:D} not claimed: {blocked} default account is blocked by a usage limit");
                continue;
            }
            var reservation = accounts?.ReserveClaim(maxAcceptedTeams, DateTimeOffset.UtcNow);
            if (accounts is not null && reservation is null)
            {
                log?.Invoke("PRFactory intake paused: accepted-team admission is full (active or account-parked teams)");
                break;
            }
            try
            {
                var (claimed, conflict) = await client.ClaimAsync(item.Id, machineId, ct);
                if (conflict)
                {
                    // Most likely the server's worker cap; further claims this tick would hit the same refusal.
                    log?.Invoke($"PRFactory claim of {item.Id:D} refused (409); stopping claims this tick");
                    break;
                }
                if (claimed is null || claimed.Id != item.Id)
                {
                    continue;
                }

                var json = JsonSerializer.Serialize(claimed, PRFactoryWorkItemJson.Default.PRFactoryWorkItem);
                teams.CreateIfAbsent(server, claimed.Id, json, machineId); // Commit identity before POST or dispatch.
            }
            finally
            {
                if (reservation is not null) { accounts!.ReleaseClaim(reservation); }
            }
            await IsolateAsync(item.Id, () => AdvanceAsync(teams.Get(server, item.Id)!, ct), ct);
        }
    }

    // Records the server cap from the latest poll; true when it is reached, so this tick claims nothing.
    bool NoteLimit()
    {
        var limit = client.Limit;
        onLimit?.Invoke(limit);
        var capped = limit?.AtCap == true;
        if (LimitPaused.TryGetValue(server, out var was) ? was != capped : capped)
        {
            LimitPaused[server] = capped;
            log?.Invoke(capped
                ? $"PRFactory intake paused: server limit reached ({limit!.Active}/{limit.Max} active work items)"
                : "PRFactory intake resumed: server limit no longer reached");
        }
        return capped;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> LimitPaused = new();

    string? BlockedBackend(PRFactoryWorkItem item)
    {
        var externalNames = repositories.SingleOrDefault(r => r.Id == item.RepositoryId)?.ExternalMembers ?? [];
        var backends = new[] { item.AgentType }.Concat((item.TeamPlan?.Members ?? [])
            .Where(m => !m.IsLead && !externalNames.Contains(m.Name, StringComparer.Ordinal))
            .Select(m => m.Backend ?? item.AgentType));
        return backends.Select(MapBackend).FirstOrDefault(backend => backend is not null
            && !accounts!.CanStart(backend, DefaultAccount, DateTimeOffset.UtcNow));
    }

    // One failing team must not block every other team and new claims; it retries next tick.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> CleanupRetryAfter = new();

    async Task IsolateAsync(Guid id, Func<Task> advance, CancellationToken ct)
    {
        try
        {
            await advance();
        }
        catch (Exception ex) when (ex is not (WorkerTokenRejectedException or OutOfMemoryException)
            && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // A 4xx is the server's answer, not a transport failure; only no status or 5xx is.
            var transport = ex is TaskCanceledException or HttpRequestException { StatusCode: null or >= System.Net.HttpStatusCode.InternalServerError };
            if (transport && authority is not null)
            {
                // Ownership is retained; further mutations wait for fresh server confirmation.
                await authority.TransportFailureAsync(id, null, CancellationToken.None);
            }
            log?.Invoke($"PRFactory work item {id:D} deferred ({ex.GetType().Name}{(ex is HttpRequestException { StatusCode: { } status } ? " " + (int)status : "")})");
        }
    }

    /// <summary>Admits one remote/local side effect under current server authority.</summary>
    async Task Guard(Guid id, Func<Task> effect, CancellationToken ct)
    {
        if (authority is null)
        {
            await effect();
            return;
        }
        if (!await authority.RunAsync(id, effect, ct))
        {
            throw new PRFactoryFencedException(id);
        }
    }

    Task Observe(Guid id, string disposition, string? reason, CancellationToken ct) =>
        authority?.ObserveAsync(id, disposition, reason, ct) ?? Task.CompletedTask;

    async Task AdvanceAsync(PRFactoryTeamRecord team, CancellationToken ct)
    {
        try
        {
            if (handovers?.Release(WorkspaceKey(team.WorkItemId)) is not null)
            {
                await Observe(team.WorkItemId, "revoked", "handover_released", ct);
                teams.Finish(server, team.WorkItemId, "completed");
                return;
            }
            if (!await ConfirmAcceptanceAsync(team, ct))
            {
                return;
            }
            team = teams.Get(server, team.WorkItemId)!;
            await AdvanceCoreAsync(team, ct);
        }
        catch (PRFactoryLeaseLostException)
        {
            if (team.AcceptanceState != "legacy")
            {
                await FenceAsync(team.WorkItemId, "lease_rejected", ct);
                return;
            }
            var externals = teams.ExternalMembers(server, team.WorkItemId);
            if (externals.Count > 0)
            {
                externalTeam!.CloseTeam(externals[0].TeamId);
                teams.MarkExternalClosed(server, team.WorkItemId);
            }
            StopManagedJobs(team.WorkItemId);
            teams.Finish(server, team.WorkItemId, "failed");
            log?.Invoke($"PRFactory work item {team.WorkItemId:D} lease_lost; local team closed");
        }
        catch (PRFactoryStreamRejectedException ex)
        {
            log?.Invoke($"PRFactory work item {team.WorkItemId:D} agent-stream rejected ({(int)ex.StatusCode}); reporting failure");
            var item = JsonSerializer.Deserialize(team.ClaimedJson, PRFactoryWorkItemJson.Default.PRFactoryWorkItem)!;
            await FinishAsync(team, item, false, $"PRFactory agent-stream rejected with HTTP {(int)ex.StatusCode}", null, ct);
        }
    }

    async Task<bool> ConfirmAcceptanceAsync(PRFactoryTeamRecord team, CancellationToken ct)
    {
        if (team.AcceptanceState == "legacy")
        {
            // Old servers: lease loss surfaces as 404/409 on the next mutation.
            await Observe(team.WorkItemId, "accepted", null, ct);
            return true;
        }
        if (team.MachineId is not Guid machine || team.AtfJobId is not { Length: > 0 } jobId)
        {
            throw new InvalidDataException("PRFactory acceptance identity is missing");
        }

        if (team.AcceptanceState is "accepted" or "reconciliation_needed")
        {
            // A fenced team keeps polling only to observe a definitive server disposition.
            var observed = await client.GetAtfAcceptanceAsync(team.WorkItemId, machine, jobId, ct);
            return await ApplyAcceptanceAsync(team, observed, ct);
        }

        var item = JsonSerializer.Deserialize(team.ClaimedJson, PRFactoryWorkItemJson.Default.PRFactoryWorkItem)
            ?? throw new InvalidDataException("Invalid persisted PRFactory claim");
        if (item.LeaseToken is not Guid lease || lease == Guid.Empty)
        {
            await FenceAsync(team.WorkItemId, "lease_missing", ct);
            return false;
        }
        var accepted = await client.AcceptAtfAsync(team.WorkItemId, machine, lease, jobId, ct);
        if (accepted.Result == PRFactoryClient.AcceptanceResult.NotFound)
        {
            var observed = await client.GetAtfAcceptanceAsync(team.WorkItemId, machine, jobId, ct);
            if (observed.Result != PRFactoryClient.AcceptanceResult.NotFound)
            {
                return await ApplyAcceptanceAsync(team, observed, ct);
            }
            // Both acceptance routes are absent or the item disappeared. The established lease
            // heartbeat distinguishes a missing feature from a lost claim before dispatch.
            if (!await client.ConfirmLegacyLeaseAsync(team.WorkItemId, lease, ct))
            {
                await FenceAsync(team.WorkItemId, "lease_lost_before_acceptance", ct);
                return false;
            }
            teams.SetAcceptance(server, team.WorkItemId, "legacy");
            client.LogLegacyOnce(log);
            await Observe(team.WorkItemId, "accepted", null, ct);
            return true;
        }
        return await ApplyAcceptanceAsync(team, accepted, ct);
    }

    async Task<bool> ApplyAcceptanceAsync(PRFactoryTeamRecord team, PRFactoryClient.Acceptance acceptance, CancellationToken ct)
    {
        var id = team.WorkItemId;
        if (acceptance.Disposition == "accepted")
        {
            var claim = JsonSerializer.Deserialize(team.ClaimedJson, PRFactoryWorkItemJson.Default.PRFactoryWorkItem)!;
            if (claim.HandoverReleaseId != acceptance.HandoverReleaseId
                || claim.HandoverRepositoryId != acceptance.HandoverRepositoryId
                || !string.Equals(claim.HandoverBaseCommitSha, acceptance.HandoverBaseCommitSha,
                    StringComparison.OrdinalIgnoreCase)
                || (claim.HandoverReleaseId is not null && (claim.StartFromBranch != acceptance.StartFromBranch
                    || !string.Equals(claim.StartCommitSha, acceptance.StartCommitSha, StringComparison.OrdinalIgnoreCase))))
            {
                await FenceAsync(id, "handover_acceptance_mismatch", ct);
                return false;
            }
        }
        switch (acceptance.Disposition)
        {
            case "accepted" when team.AcceptanceState != "reconciliation_needed":
                teams.SetAcceptance(server, id, "accepted");
                await Observe(id, "accepted", null, ct);
                return true;
            case "accepted":
                return false; // Fencing is sticky; ordinary acceptance never reopens it.
            case "completed" or "cancelled" or "revoked":
                // Definitive: stop owned execution (retried until quiescent) and close the local team.
                await Observe(id, acceptance.Disposition, acceptance.Reason, ct);
                teams.Finish(server, id, acceptance.Disposition == "completed" ? "completed" : "failed");
                log?.Invoke($"PRFactory work item {id:D} {acceptance.Disposition} by server; local execution stopped");
                return false;
            default:
                await FenceAsync(id, acceptance.Reason ?? "reconciliation_needed", ct);
                return false;
        }
    }

    async Task FenceAsync(Guid id, string reason, CancellationToken ct)
    {
        teams.SetAcceptance(server, id, "reconciliation_needed");
        await Observe(id, "reconciliation-needed", reason, ct);
        log?.Invoke($"PRFactory work item {id:D} reconciliation needed ({reason}); dispatch and publication fenced");
    }

    async Task AdvanceCoreAsync(PRFactoryTeamRecord team, CancellationToken ct)
    {
        var item = JsonSerializer.Deserialize(team.ClaimedJson, PRFactoryWorkItemJson.Default.PRFactoryWorkItem)
            ?? throw new InvalidDataException("Invalid persisted PRFactory claim");
        var multiRepo = PRFactoryRepositorySet.HasSecondaries(item);
        if (multiRepo && (repositorySets is null || !await client.SupportsMultiRepoAsync(ct)))
        {
            await FinishAsync(team, item, false, "multi-repository work items are unsupported", null, ct);
            return;
        }
        var repo = repositories.SingleOrDefault(r => r.Id == item.RepositoryId);
        if (item.RepositoryId is not null && repo is null
            || item.RepositoryId is null && (workspaces is null || workspaceRoot is null
                || !allowRepoLess && workspaces.Get(WorkspaceKey(item.Id)) is null))
        {
            await FinishAsync(team, item, false, "repository has no approved local mapping", null, ct);
            return;
        }
        var plan = item.TeamPlan;
        var members = (plan?.Members ?? []).Where(m => !m.IsLead).OrderBy(m => m.Order).ToArray();
        // A repo's external mapping applies only to members this item's recipe declares.
        var externalNames = (repo?.ExternalMembers ?? []).Where(name => members.Any(m => m.Name == name)).ToArray();
        if (members.Any(m => m.Name == "lead") || members.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count() != members.Length
            || members.Any(m => string.IsNullOrWhiteSpace(m.Name) || m.MaxIterations is < 1)
            || (plan is not null && (plan.MaxConcurrentChildren < 0 || plan.FreeRoomCeiling < 0)))
        {
            await FinishAsync(team, item, false, "invalid team recipe", repo?.Directory, ct);
            return;
        }
        if (MapBackend(item.AgentType) is null || members.Any(m => !externalNames.Contains(m.Name, StringComparer.Ordinal)
            && MapBackend(m.Backend ?? item.AgentType) is null))
        {
            await FinishAsync(team, item, false, "unsupported agent backend", repo?.Directory, ct);
            return;
        }
        if (externalNames.Length > 0 && externalTeam is null)
        {
            await FinishAsync(team, item, false, "external team members are unavailable on this connector", repo?.Directory, ct);
            return;
        }
        // The recipe's free room is reserved for later command-driven turns. This slice submits
        // only declared members. MaxIterations is persisted in the claim and gates later turns.
        var managedMembers = members.Where(m => !externalNames.Contains(m.Name, StringComparer.Ordinal)).ToArray();
        var maxChildren = plan is null ? 0 : Math.Min(plan.MaxConcurrentChildren, managedMembers.Length);
        if (managedMembers.Length > 0 && maxChildren == 0)
        {
            await FinishAsync(team, item, false, "team recipe permits no concurrent children", repo?.Directory, ct);
            return;
        }
        WorkspaceSnapshot? workspace = null;
        var baseWip = handovers is not null && item.RepositoryId is not null
            && await client.SupportsBaseWipAsync(ct);
        if (!baseWip && handovers is not null
            && (handovers.Refresh(WorkspaceKey(item.Id)) is not null || handovers.Wip(WorkspaceKey(item.Id)) is not null))
        {
            throw new HttpRequestException("PRFactory base-wip-v1 capability disappeared during accepted work.");
        }
        if (multiRepo) { await repositorySets!.RecoverRefreshAsync(WorkspaceKey(item.Id)); }
        if (baseWip && workspaces is not null)
        {
            // Local rollback of ATF's own interrupted refresh; the half-rebased lead cannot be re-materialized.
            await workspaces.Freshness(handovers!).RecoverAsync(WorkspaceKey(item.Id));
        }
        if (workspaces is not null)
        {
            try
            {
                if (multiRepo)
                {
                    _ = await PRFactoryRepositorySet.ParseAsync(item, repositories, WorkspaceKey(item.Id));
                }
                await Guard(item.Id, async () => workspace = await PrepareWorkspaceAsync(item, repo, managedMembers, baseWip, ct), ct);
                if (multiRepo)
                {
                    await Guard(item.Id, async () => _ = await repositorySets!.PrepareAsync(WorkspaceKey(item.Id),
                        item, repositories, [.. managedMembers.Select(m => m.Name)]), ct);
                }
            }
            catch (InvalidOperationException ex)
            {
                // Identity mismatch, missing continuation SHA or an unreachable branch: fail visibly, keep files.
                await FinishAsync(team, item, false, $"workspace preparation failed: {ex.Message}", repo?.Directory, ct);
                return;
            }
        }
        if (baseWip && team.MachineId is Guid handoverMachine && team.AtfJobId is { Length: > 0 } handoverJob
            && await client.GetHandoverRequestAsync(item.Id, handoverMachine, handoverJob, ct) is { } request)
        {
            if (await HandleHandoverAsync(team, item, workspace, request, ct)) { return; }
            // A phase that publishes no branch has nothing to hand over; its request is moot, so finish normally.
        }
        if ((baseWip || multiRepo) && workspace is { RepositoryPath: not null }
            && teams.MemberJob(server, item.Id, "lead", 0) is null)
        {
            if (multiRepo)
            {
                var set = repositorySets!.Get(workspace.Key)!;
                IReadOnlyList<(RepositorySetMember Entry, BaseFreshnessResult Result)> results = [];
                await Guard(item.Id, async () => results = await repositorySets.RefreshAsync(set, item.PlanBasisCommitSha), ct);
                foreach (var (entry, refreshed) in results)
                {
                    if (item.LeaseToken is not Guid multiLease) { throw new InvalidOperationException("Missing repository lease."); }
                    var repository = Guid.Parse(entry.Id);
                    if (refreshed.ConflictingPaths.Length > 0)
                    {
                        await Guard(item.Id, () => client.ReportBaseConflictAsync(item.Id,
                            new([repository], refreshed.ConflictingPaths, multiLease), ct), ct);
                    }
                    var action = refreshed.Action switch { "Fetched" => 1, "Rebased" => 2, "ConflictStopped" => 3, _ => 0 };
                    var repositoryWorkspace = workspaces!.Get(entry.WorkspaceKey)!;
                    await Guard(item.Id, () => client.ReportFreshnessAsync(item.Id,
                        new(multiLease, repository, refreshed.AgentMayRun ? refreshed.CurrentBaseSha : refreshed.RecordedBaseSha,
                            repositoryWorkspace.InternalBranch!, refreshed.HeadSha, 0,
                            refreshed.AgentMayRun ? null : "Base drift requires checkpoint or conflict resolution",
                            repositoryWorkspace.BaseBranch!, refreshed.CurrentBaseSha, refreshed.CommitsBehind, action), ct), ct);
                }
                if (results.Count != set.Members.Length || results.Any(r => !r.Result.AgentMayRun)) { return; }
                workspace = workspaces!.Get(workspace.Key)!;
            }
            else
            {
                BaseFreshnessResult freshness = null!;
                await Guard(item.Id, async () => freshness = await workspaces!.Freshness(handovers!)
                    .EnsureFreshAsync(workspace, item.PlanBasisCommitSha), ct);
                workspace = workspaces!.Get(workspace.Key)!;
                if (item.LeaseToken is not Guid lease || item.RepositoryId is not Guid repository)
                {
                    throw new InvalidOperationException("Base refresh lacks accepted repository identity.");
                }

                if (freshness.ConflictingPaths.Length > 0)
                {
                    await Guard(item.Id, () => client.ReportBaseConflictAsync(item.Id,
                        new([repository], freshness.ConflictingPaths, lease), ct), ct);
                }

                var action = freshness.Action switch { "Fetched" => 1, "Rebased" => 2, "ConflictStopped" => 3, _ => 0 };
                await Guard(item.Id, () => client.ReportFreshnessAsync(item.Id,
                    new(lease, repository, freshness.AgentMayRun ? freshness.CurrentBaseSha : freshness.RecordedBaseSha,
                        workspace.InternalBranch!, freshness.HeadSha, 0,
                        freshness.AgentMayRun ? null : "Base drift requires a checkpoint or conflict resolution",
                        workspace.BaseBranch!, freshness.CurrentBaseSha, freshness.CommitsBehind, action), ct), ct);
                if (!freshness.AgentMayRun)
                {
                    return;
                }
            }
        }
        string Cwd(string member) => workspace is null ? repo!.Directory
            : member == "lead" ? workspace.LeadPath : workspace.Members.Single(m => m.Name == member).Path;
        var baseInstruction = multiRepo ? item.Prompt + "\n\nRepository checkout manifest: "
            + repositorySets!.Get(WorkspaceKey(item.Id))!.ManifestPath
            + "\nUse your lead or child paths from this manifest. Commit changes in each writable repository."
            : item.Prompt;
        // The prompts reference these; there is no env plumbing, so each member's own primary path is stated in text.
        string Instruction(string member) => baseInstruction + "\n\nPRFACTORY_PRIMARY_REPO_PATH=" + Path.GetFullPath(Cwd(member))
            + (multiRepo ? "\nPRFACTORY_WORKSPACE_MANIFEST=" + repositorySets!.Get(WorkspaceKey(item.Id))!.ManifestPath : "");
        var instruction = Instruction("lead");
        JobRecord? lead;
        try
        {
            lead = await SubmitMember(item, "lead", item.AgentType, item.Model, item.Effort,
                instruction, Cwd("lead"), workspace is not null, ct);
        }
        catch (PRFactoryJobSubmissionException ex)
        {
            await FinishAsync(team, item, false, ex.Message, repo?.Directory, ct);
            return;
        }
        if (lead is null)
        {
            return;
        }

        var externalRepliesDrained = externalNames.Length == 0 || await AdvanceExternalAsync(item, externalNames, ct);

        var active = 0;
        var allJobs = new List<JobRecord> { lead };
        foreach (var member in managedMembers)
        {
            var mapped = teams.MemberJob(server, item.Id, member.Name, 0);
            if (mapped is not null)
            {
                var existing = getJob(mapped) ?? throw new InvalidDataException("Persisted PRFactory job is missing");

                allJobs.Add(existing);
                if (existing.Status is JobStatus.Queued or JobStatus.Running)
                {
                    active++;
                }

                continue;
            }
            if (active >= maxChildren)
            {
                continue;
            }

            var memberInstruction = $"{Instruction(member.Name)}\n\nRole: {member.Role}\nMember: {member.Name}"
                + (string.IsNullOrWhiteSpace(member.Notes) ? "" : $"\nNotes: {member.Notes}");
            JobRecord? child;
            try
            {
                child = await SubmitMember(item, member.Name, member.Backend ?? item.AgentType,
                    member.Model ?? item.Model, member.Effort ?? item.Effort, memberInstruction, Cwd(member.Name), workspace is not null, ct);
            }
            catch (PRFactoryJobSubmissionException ex)
            {
                await FinishAsync(team, item, false, ex.Message, repo?.Directory, ct);
                return;
            }
            if (child is null)
            {
                return;
            }

            allJobs.Add(child);
            if (child.Status is JobStatus.Queued or JobStatus.Running)
            {
                active++;
            }
        }
        await AdvanceCommandsAsync(item, ct);
        await AdvanceHumanWaitsAsync(item, ct);
        var outputDrained = await UploadManagedAsync(item, ct);
        if (baseWip && workspace is { RepositoryPath: not null } && handovers is not null
            && BranchPublisher.ShouldPublish(workspace.ReadOnly || item.ReadOnly,
                string.Equals(item.TicketSource, "ProjectInit", StringComparison.OrdinalIgnoreCase), item.Type)
            && team.MachineId is Guid machine && team.AtfJobId is { Length: > 0 } atfJob
            && item.LeaseToken is Guid wipLease && item.RepositoryId is Guid wipRepo)
        {
            var publisher = new WipPublisher(handovers, async (id, effect, token) =>
            {
                await Guard(id, effect, token);
                return true;
            });
            var branch = WipPublisher.BranchName(Environment.MachineName,
                item.TicketKey ?? throw new InvalidOperationException("WIP ticket key missing."));
            try
            {
                await publisher.PublishAsync(item.Id, workspace, branch, async (wipBranch, head) =>
                {
                    var countText = await TeamWorkspace.Git(workspace.LeadPath, "rev-list", "--count", workspace.BaseSha + ".." + head);
                    var report = new PRFactoryWipReport(wipLease, machine, atfJob, wipRepo, workspace.BaseSha!,
                        wipBranch, head, int.Parse(countText, System.Globalization.CultureInfo.InvariantCulture), true, null,
                        $"{item.Id:D}:{head}");
                    string? receipt = null;
                    await Guard(item.Id, async () => receipt = await client.ReportWipAsync(item.Id, report, ct), ct);
                    return receipt!;
                }, allowRewrite: handovers.Refresh(workspace.Key)?.Action == "Rebased", ct: ct);
            }
            catch (WipPushException ex)
            {
                // Reported, not rethrown: a failed WIP push leaves no receipt (so no release), but must not
                // stall the lead's completion and final publication.
                var countText = await TeamWorkspace.Git(workspace.LeadPath, "rev-list", "--count", workspace.BaseSha + ".." + ex.HeadSha);
                try
                {
                    await Guard(item.Id, () => client.ReportWipFailureAsync(item.Id,
                        new(wipLease, machine, atfJob, wipRepo, workspace.BaseSha!, ex.Branch, ex.HeadSha,
                            int.Parse(countText, System.Globalization.CultureInfo.InvariantCulture), false, ex.Message,
                            $"{item.Id:D}:{ex.HeadSha}"), ct), ct);
                }
                catch (PRFactoryWipRejectedException rejected)
                {
                    log?.Invoke($"PRFactory work item {item.Id:D} WIP failure report rejected ({rejected.Status}: {rejected.Error})");
                }
                log?.Invoke($"PRFactory work item {item.Id:D} WIP publication failed; no receipt recorded");
            }
            catch (PRFactoryWipRejectedException ex)
            {
                // Recorded as rejected for this head by the publisher: no re-report until the head changes.
                log?.Invoke($"PRFactory work item {item.Id:D} WIP publication rejected ({ex.Status}: {ex.Error}); no receipt, handover disabled");
            }
            catch (InvalidOperationException ex)
            {
                log?.Invoke($"PRFactory work item {item.Id:D} WIP publication deferred: {ex.Message}");
            }
        }
        allJobs = [.. teams.ManagedMembers(server, item.Id).GroupBy(m => m.Member)
            .Select(g => getJob(g.Last().JobId)!)];
        lead = allJobs.First(j => j.JobId == teams.ManagedMembers(server, item.Id).Last(m => m.Member == "lead").JobId);
        if (accounts is not null && await ResumeParkedAsync(item, allJobs, ct))
        {
            return; // An account-parked member holds completion until it resumes in the same session.
        }
        var waitForManaged = managedMembers.Length > 0 || externalNames.Length == 0;
        if (allJobs.Count != managedMembers.Length + 1 || !externalRepliesDrained || !outputDrained
            || (workspace is { ReadOnly: false } && allJobs.Any(j => j.Status is JobStatus.Queued or JobStatus.Running or JobStatus.NeedsReconciliation))
            || (waitForManaged
                ? allJobs.Any(j => j.Status is JobStatus.Queued or JobStatus.Running or JobStatus.NeedsReconciliation)
                : teams.ExternalMembers(server, item.Id).Any(e => !e.Closed)))
        {
            return;
        }

        if (externalNames.Length > 0)
        {
            var externals = teams.ExternalMembers(server, item.Id);
            var open = externals.Where(e => !e.Closed).ToList();
            if (open.Count > 0)
            {
                // Revoke first so no reply can land after the drain; the next tick uploads the rest and closes.
                foreach (var external in open)
                {
                    externalTeam!.RevokeMember(external.TeamId, external.ActualName);
                    teams.MarkExternalClosed(server, item.Id, external.Member);
                }
                return;
            }
        }
        // After revoking: messages still waiting for an unjoined member settle as member_closed next tick.
        if (teams.PendingCommands(server, item.Id).Count > 0)
        {
            return;
        }
        if (externalNames.Length > 0)
        {
            var externals = teams.ExternalMembers(server, item.Id);
            externalTeam!.CloseTeam(externals[0].TeamId);
            teams.MarkExternalClosed(server, item.Id);
        }
        if (BlockingWait(item.Id, out var waitFailed) is { } blocking)
        {
            if (waitFailed)
            {
                await FinishAsync(team, item, false, $"human wait {blocking.QuestionId} {blocking.Status}: {blocking.Error}", repo?.Directory, ct);
            }
            return; // A question is open: the answer resumes the saved session before completion.
        }
        var failed = allJobs.FirstOrDefault(j => j.Status != JobStatus.Completed);
        if (failed is null && workspace is { RepositoryPath: not null, ReadOnly: false } && managedMembers.Length > 0
            && teams.MemberJob(server, item.Id, "lead", FinalizeTurn) is null)
        {
            await IntegrateAndFinalizeAsync(team, item, workspace, lead, ct);
            return;
        }
        await FinishAsync(team, item, failed is null || !waitForManaged && lead.Status is JobStatus.Queued or JobStatus.Running or JobStatus.NeedsReconciliation,
            failed?.ReasonCode == "agent_rate_limited" ? "agent_rate_limited: " + failed.ResultText
                : failed?.ReasonCode ?? "job failed", repo?.Directory, ct,
            lead.ResultText ?? (!waitForManaged ? "External members completed their work; replies are in the agent stream." : null));
    }

    /// <summary>"KEY-1_refinement_lead": agent names allow only letters, digits, '-' and '_' and start with a letter or digit.
    /// A name that had to be cleaned, clipped or given a fallback key is clipped and suffixed with a short hash of the original
    /// (key|phase|member), so distinct inputs stay distinct and the result is always valid.</summary>
    internal static string JobName(PRFactoryWorkItem item, string member)
    {
        static string Clean(string text) => new([.. text.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')]);
        var originalKey = item.TicketKey ?? "";
        var originalPhase = item.Type ?? "";
        var phase = Clean(originalPhase);
        if (phase.StartsWith("Ticket", StringComparison.Ordinal)) { phase = phase["Ticket".Length..]; }
        var key = Clean(originalKey);
        var altered = key != originalKey || Clean(member) != member || key.Length == 0;
        if (key.Length == 0) { key = item.Id.ToString("N")[..8]; originalKey = item.Id.ToString("N"); }
        var readable = string.Join('_', new[] { key, phase.ToLowerInvariant(), Clean(member) }.Where(p => p.Length > 0));
        if (!altered && readable.Length <= 64 && char.IsAsciiLetterOrDigit(readable[0])) { return readable; }
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{originalKey}|{originalPhase}|{member}")))[..6].ToLowerInvariant();
        var head = readable.TrimStart('-', '_');
        if (head.Length > 57) { head = head[..57]; }
        return head.Length == 0 ? hash : head + "_" + hash;
    }

    async Task<JobRecord?> SubmitMember(PRFactoryWorkItem item, string member,
        PRFactoryAgentType agent, string? model, PRFactoryEffort? effort, string instruction, string cwd, bool isolated, CancellationToken ct)
    {
        var existingId = teams.MemberJob(server, item.Id, member, 0);
        if (existingId is not null)
        {
            return getJob(existingId) ?? throw new InvalidDataException("Persisted PRFactory job is missing");
        }

        var backend = MapBackend(agent) ?? throw new InvalidDataException($"Unsupported PRFactory backend: {agent}");
        // The same key and exact request resolve a lost local acceptance response to one job.
        var prefix = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(server)))[..12];
        var memberKey = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(member)))[..12];
        var key = $"prf:{prefix}:{item.Id:N}:{memberKey}:0";
        var result = JobResult.Fail(JobErrors.DaemonUnhealthy);
        await Guard(item.Id, () =>
        {
            result = submit(new SubmitJobRequest(key, instruction, null, false)
            {
                Backend = backend,
                Cwd = cwd,
                // A prepared team workspace is already isolated; never re-pin a second worktree from incidental HEAD.
                Worktree = !isolated && !item.ReadOnly,
                Model = model,
                Effort = effort?.ToString().ToLowerInvariant(),
                TargetAgent = JobName(item, member),
                LeadSessionId = leadSessionFor?.Invoke(cwd),
            });
            // The member mapping is part of the admitted effect: a concurrent fence must see it.
            if (result.Error is null) { teams.RecordMember(server, item.Id, member, 0, result.Job!.JobId); }
            return Task.CompletedTask;
        }, ct);
        if (result.Error is JobErrors.QueueFull or JobErrors.StorageBusy or JobErrors.DaemonUnhealthy)
        {
            return null;
        }
        if (result.Error is not null)
        {
            throw new PRFactoryJobSubmissionException($"PRFactory job submit: {result.Error}");
        }

        var jobId = result.Job!.JobId;
        if (result.Outcome == "accepted")
        {
            onAccepted();
        }

        return getJob(jobId);
    }

    /// <summary>Resumes due parks as a same-session follow-up; returns true while any member park is open.</summary>
    async Task<bool> ResumeParkedAsync(PRFactoryWorkItem item, IReadOnlyList<JobRecord> latest, CancellationToken ct)
    {
        // A crash can follow the new member mapping but precede the park receipt. The
        // old turn is no longer in 'latest', so reconcile its fixed-key successor first.
        var turns = teams.ManagedMembers(server, item.Id).Select(m => getJob(m.JobId)).OfType<JobRecord>().ToList();
        foreach (var turn in turns.Where(t => t.ParentJobId is not null && t.IdempotencyKey == "prf-resume:" + t.ParentJobId))
        {
            if (accounts!.Park(turn.ParentJobId!)?.State == "resuming") { accounts.ResumeRecorded(turn.ParentJobId!); }
        }
        var held = false;
        foreach (var job in latest)
        {
            if (accounts!.Park(job.JobId) is not { State: not "resumed", SessionId: not null } park)
            {
                continue;
            }
            held = true;
            if (park.State == "parked" && !accounts.TryBeginResume(park, DateTimeOffset.UtcNow))
            {
                continue; // Not yet reset (or unknown reset awaiting explicit recovery).
            }
            var member = teams.ManagedMembers(server, item.Id).Last(m => m.JobId == job.JobId);
            var resumed = JobResult.Fail(JobErrors.DaemonUnhealthy);
            // A fixed key makes a crash between acceptance and ResumeRecorded resolve to the same turn.
            await Guard(item.Id, () =>
            {
                resumed = followUp?.Invoke(new FollowUpRequest(job.JobId,
                    "The account usage limit has reset. Continue the task exactly where you left off.", "prf-resume:" + job.JobId))
                    ?? JobResult.Fail(JobErrors.DaemonUnhealthy);
                if (resumed.Error is null && !teams.ManagedMembers(server, item.Id).Any(m => m.JobId == resumed.Job!.JobId))
                {
                    teams.RecordMember(server, item.Id, member.Member,
                        teams.ManagedMembers(server, item.Id).Where(m => m.Member == member.Member).Max(m => m.Turn) + 1, resumed.Job!.JobId);
                }
                return Task.CompletedTask;
            }, ct);
            if (resumed.Error is null)
            {
                accounts.ResumeRecorded(job.JobId);
                onAccepted();
            }
            else
            {
                accounts.RetryResume(job.JobId);
            }
        }
        return held;
    }

    async Task<PublicationReceipt?> PublishAsync(PRFactoryTeamRecord team, PRFactoryWorkItem item, WorkspaceSnapshot? workspace,
        CancellationToken ct)
    {
        if (publications is null || workspace is not
            {
                RepositoryId: { } repositoryId, Remote: { } remote, InternalBranch: { } internalBranch,
                PublishBranch: { } publishBranch, BaseSha: { } baseSha
            })
        {
            return null; // Scratch or unconfigured: nothing to publish, never an invented repository.
        }
        var request = new PublicationRequest($"{server}|{item.Id:D}|{repositoryId}", server, item.Id, item.LeaseToken ?? Guid.Empty,
            team.MachineId?.ToString("D") ?? "legacy", team.AtfJobId ?? "legacy", repositoryId, workspace.Key, workspace.LeadPath,
            remote, internalBranch, publishBranch, baseSha, item.Type ?? "", workspace.ReadOnly || item.ReadOnly,
            string.Equals(item.TicketSource, "ProjectInit", StringComparison.OrdinalIgnoreCase), item.TicketArtefactFolder);
        if (!BranchPublisher.ShouldPublish(request))
        {
            return null;
        }
        using var publisher = new BranchPublisher(publications, async (id, effect, token) =>
        {
            if (authority is null)
            {
                await effect();
                return true;
            }
            // Unconfirmed authority defers (retried next tick), it never fails the phase.
            return await authority.RunAsync(id, effect, token) ? true : throw new PRFactoryFencedException(id);
        });
        return await publisher.PublishAsync(request, ct);
    }

    async Task<(PublicationReceipt? Primary, List<PRFactoryRepositoryFreshnessRequest> Results)> PublishSetAsync(
        PRFactoryTeamRecord team, PRFactoryWorkItem item, RepositorySetSnapshot set, CancellationToken ct)
    {
        if (publications is null || item.LeaseToken is not Guid lease)
        {
            throw new InvalidOperationException("Multi-repository publication store or lease missing.");
        }
        using var publisher = new BranchPublisher(publications, async (id, effect, token) =>
        {
            if (authority is null) { await effect(); return true; }
            return await authority.RunAsync(id, effect, token) ? true : throw new PRFactoryFencedException(id);
        });
        var requests = new List<(RepositorySetMember Entry, WorkspaceSnapshot Workspace, PublicationRequest Request)>();
        foreach (var entry in set.Members)
        {
            var workspace = workspaces!.Get(entry.WorkspaceKey)!;
            var request = new PublicationRequest($"{server}|{item.Id:D}|{entry.Id}", server, item.Id, lease,
                team.MachineId?.ToString("D") ?? "legacy", team.AtfJobId ?? "legacy", entry.Id,
                workspace.Key, workspace.LeadPath, entry.Remote, workspace.InternalBranch!, workspace.PublishBranch!,
                workspace.BaseSha!, item.Type ?? "", entry.ReadOnly, string.Equals(item.TicketSource, "ProjectInit", StringComparison.OrdinalIgnoreCase),
                item.TicketArtefactFolder);
            requests.Add((entry, workspace, request));
        }
        // All intended heads are frozen durably before the first remote mutation.
        foreach (var (entry, workspace, request) in requests)
        {
            // A read-only checkout may start from a continuation branch; only changes after that start count.
            if (entry.ReadOnly && (JobWorktree.Head(workspace.LeadPath) != workspace.StartingSha
                || (await TeamWorkspace.Git(workspace.LeadPath, "status", "--porcelain", "--untracked-files=all")).Length != 0))
            {
                throw new InvalidOperationException($"Read-only repository {entry.Name} changed; local output retained.");
            }
            await publisher.FreezeAsync(request, ct);
        }
        var results = new List<PRFactoryRepositoryFreshnessRequest>();
        PublicationReceipt? primary = null;
        foreach (var (entry, workspace, request) in requests)
        {
            var head = JobWorktree.Head(workspace.LeadPath)!;
            var writable = BranchPublisher.ShouldPublish(request);
            var changed = head != workspace.BaseSha;
            var state = !writable || !changed ? 1 : 2; // Skipped or Pushed.
            var reason = !writable ? "read-only repository" : !changed ? "unchanged repository" : null;
            PublicationReceipt? receipt = null;
            if (state == 2)
            {
                try { receipt = await publisher.PublishAsync(request, ct); }
                catch (InvalidOperationException ex)
                {
                    var failed = new PRFactoryRepositoryFreshnessRequest(lease, Guid.Parse(entry.Id), workspace.BaseSha!,
                        request.PublishBranch, head, 3, ex.Message, workspace.BaseBranch!, workspace.BaseSha!, 0, 0);
                    await Guard(item.Id, () => client.ReportFreshnessAsync(item.Id, failed, ct), ct);
                    throw new HttpRequestException($"Repository {entry.Name} push unresolved; retry after remote evidence.", ex);
                }
            }
            var outcome = new PRFactoryRepositoryFreshnessRequest(lease, Guid.Parse(entry.Id), workspace.BaseSha!,
                request.PublishBranch, state == 2 ? head : null, state, reason, workspace.BaseBranch!, workspace.BaseSha!, 0, 0);
            await Guard(item.Id, () => client.ReportFreshnessAsync(item.Id, outcome, ct), ct);
            results.Add(outcome);
            if (entry.WorkspaceKey == set.WorkspaceKey) { primary = receipt; }
        }
        if (results.Count != set.Members.Length || results.Any(r => r.PushState is not (1 or 2)))
        {
            throw new InvalidOperationException("Every repository must have a successful or skipped result before completion.");
        }
        return (primary, results);
    }

    string WorkspaceKey(Guid id) => $"{server}|{id:D}";

    /// <summary>False when the phase publishes no branch: nothing to hand over, the caller proceeds normally.</summary>
    async Task<bool> HandleHandoverAsync(PRFactoryTeamRecord team, PRFactoryWorkItem item,
        WorkspaceSnapshot? workspace, PRFactoryHandoverRequest request, CancellationToken ct)
    {
        if (!BranchPublisher.ShouldPublish((workspace?.ReadOnly ?? false) || item.ReadOnly,
            string.Equals(item.TicketSource, "ProjectInit", StringComparison.OrdinalIgnoreCase), item.Type))
        {
            return false;
        }
        if (workspace is not { RepositoryPath: not null, ReadOnly: false } || handovers is null
            || item.LeaseToken is not Guid lease || item.RepositoryId is not Guid repository
            || team.MachineId is not Guid machine || team.AtfJobId is not { Length: > 0 } atfJob)
        {
            log?.Invoke($"PRFactory work item {item.Id:D} handover deferred: repository workspace unavailable");
            return true;
        }
        bool Quiescent() => teams.MemberJobs(server, item.Id).All(id => getJob(id)?.Status is not
                (JobStatus.Queued or JobStatus.Running or JobStatus.NeedsReconciliation))
            && teams.ExternalMembers(server, item.Id).All(member => member.Closed);
        if (!Quiescent()) { return true; } // Let active turns reach a terminal state; never interrupt dirty buffers.
        if (PRFactoryRepositorySet.HasSecondaries(item))
        {
            // Only the primary lead is published and released; secondary checkouts would be left behind.
            log?.Invoke($"PRFactory work item {item.Id:D} handover held: multi-repository handover is unsupported");
            return true;
        }

        try
        {
            foreach (var path in new[] { workspace.LeadPath }.Concat(workspace.Members.Select(m => m.Path)))
            {
                if ((await TeamWorkspace.Git(path, "status", "--porcelain", "--untracked-files=all")).Length != 0)
                {
                    throw new InvalidOperationException("Uncommitted work remains; handover held.");
                }
            }
            var publisher = new WipPublisher(handovers, async (id, effect, token) =>
            {
                await Guard(id, effect, token);
                return true;
            });
            var branch = WipPublisher.BranchName(Environment.MachineName,
                item.TicketKey ?? throw new InvalidOperationException("WIP ticket key missing."));
            await publisher.PublishAsync(item.Id, workspace, branch, async (wipBranch, head) =>
            {
                var count = await TeamWorkspace.Git(workspace.LeadPath, "rev-list", "--count", workspace.BaseSha + ".." + head);
                var report = new PRFactoryWipReport(lease, machine, atfJob, repository, workspace.BaseSha!,
                    wipBranch, head, int.Parse(count, System.Globalization.CultureInfo.InvariantCulture),
                    true, null, $"{item.Id:D}:{head}:{request.RequestId}");
                string? receipt = null;
                await Guard(item.Id, async () => receipt = await client.ReportWipAsync(item.Id, report, ct), ct);
                return receipt!;
            }, allowRewrite: handovers.Refresh(workspace.Key)?.Action == "Rebased", forceReport: true, ct: ct);

            var coordinator = new PRFactoryHandover(client, handovers, async (id, effect, token) =>
            {
                await Guard(id, effect, token);
                return true;
            });
            await coordinator.ReleaseAsync(item, workspace, machine, atfJob, request.Reason, Quiescent, ct);
            await Observe(item.Id, "revoked", "handover_released", ct);
            teams.Finish(server, item.Id, "completed");
            try
            {
                await coordinator.CleanupReleasedAsync(workspace, Quiescent, TimeSpan.Zero);
            }
            catch (InvalidOperationException ex)
            {
                log?.Invoke($"PRFactory work item {item.Id:D} released; cleanup retained workspace: {ex.Message}");
            }
        }
        catch (InvalidOperationException ex)
        {
            log?.Invoke($"PRFactory work item {item.Id:D} handover held: {ex.Message}");
        }
        return true;
    }

    async Task<WorkspaceSnapshot> PrepareWorkspaceAsync(PRFactoryWorkItem item, RepositoryMapping? repo,
        PRFactoryTeamMember[] members, bool baseWip, CancellationToken ct)
    {
        var handover = item.HandoverReleaseId is not null || item.HandoverRepositoryId is not null
            || item.HandoverBaseCommitSha is not null;
        if (handover && (!baseWip || string.IsNullOrWhiteSpace(item.HandoverReleaseId)
            || item.HandoverRepositoryId != item.RepositoryId
            || item.HandoverBaseCommitSha is not { } handoverBase || !JobWorktree.IsCommitSha(handoverBase)
            || item.StartCommitSha is not { } handoverSha || !JobWorktree.IsCommitSha(handoverSha)
            || item.StartFromBranch is not { } branch || !branch.StartsWith("wip/", StringComparison.Ordinal)
            || item.Continuation is not null))
        {
            throw new InvalidOperationException("Handover release and repository snapshot are incomplete or mismatched.");
        }
        var key = WorkspaceKey(item.Id);
        var names = members.Select(m => m.Name).ToArray();
        var root = workspaceRoot ?? throw new InvalidOperationException("Workspace root is not configured.");
        if (workspaces!.Get(key) is { } saved)
        {
            if (handover && (saved.RepositoryId != item.HandoverRepositoryId!.Value.ToString("D")
                || saved.StartingSha != item.StartCommitSha))
            {
                throw new InvalidOperationException("Saved workspace differs from the handover release.");
            }
            // Recorded choices are immutable; recovery only re-materializes the owned checkouts.
            return await workspaces.PrepareAsync(new WorkspaceRequest(key, root, saved.RepositoryId, saved.RepositoryPath,
                saved.Remote, saved.BaseBranch, saved.PublishBranch, saved.ReadOnly, names));
        }
        ct.ThrowIfCancellationRequested();
        if (item.RepositoryId is null)
        {
            return await workspaces.PrepareAsync(new WorkspaceRequest(key, root, null, null, null, null, null,
                item.ReadOnly, names));
        }
        if (repo is null) { throw new InvalidOperationException("Repository mapping missing."); }
        var projectInit = string.Equals(item.TicketSource, "ProjectInit", StringComparison.OrdinalIgnoreCase);
        // Same naming as PRFactory's InitBranchNaming when an older server omits PublishBranch.
        var publish = item.PublishBranch is { Length: > 0 } explicitBranch ? explicitBranch.Trim()
            : projectInit && item.TicketKey is { Length: > 0 } ticketKey ? $"init/{ticketKey.Trim()}" : $"prfactory/{item.Id}";
        // Older servers send ProjectInit's own publish branch without a SHA; resume it as before.
        var startFrom = projectInit && item.StartFromBranch == publish && item.StartCommitSha is null ? null : item.StartFromBranch;
        return await workspaces.PrepareAsync(new WorkspaceRequest(key, root, item.RepositoryId.Value.ToString("D"), repo.Directory,
            repo.Remote ?? await TeamWorkspace.OriginAsync(repo.Directory),
            item.BaseSnapshot?.Branch ?? repo.BaseBranch ?? await TeamWorkspace.DefaultBranchAsync(repo.Directory),
            publish, item.ReadOnly, names, PriorBranch: item.Continuation?.Branch,
            PriorSha: item.Continuation?.CommitSha, StartFromBranch: startFrom,
            StartCommitSha: item.StartCommitSha, ProjectInit: projectInit,
            ExpectedBaseSha: item.BaseSnapshot?.CommitSha, ExactWipTip: baseWip));
    }

    /// <summary>Children are quiescent and succeeded: integrate their commits, stage their documents, then one lead pass.</summary>
    async Task IntegrateAndFinalizeAsync(PRFactoryTeamRecord team, PRFactoryWorkItem item, WorkspaceSnapshot workspace,
        JobRecord lead, CancellationToken ct)
    {
        try
        {
            await Guard(item.Id, () => workspaces!.IntegrateChildrenAsync(workspace, item.TicketArtefactFolder), ct);
            if (repositorySets?.Get(workspace.Key) is { } set)
            {
                foreach (var entry in set.Members.Skip(1).Where(e => !e.ReadOnly))
                {
                    await Guard(item.Id, () => workspaces!.IntegrateChildrenAsync(workspaces.Get(entry.WorkspaceKey)!,
                        item.TicketArtefactFolder), ct);
                }
            }
            foreach (var member in workspace.Members)
            {
                var folder = string.IsNullOrWhiteSpace(item.TicketArtefactFolder) ? null : Path.Combine(member.Path, item.TicketArtefactFolder);
                if (folder is null || !Directory.Exists(folder)) { continue; }
                var documents = Directory.EnumerateFiles(folder)
                    .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".md" or ".html" or ".json")
                    .Select(f => Path.GetRelativePath(member.Path, f)).Order(StringComparer.Ordinal).ToArray();
                workspaces!.GatherDocuments(workspace, member.Order, documents);
            }
        }
        catch (WorkspaceConflictException ex)
        {
            await FinishAsync(team, item, false, $"child integration conflict in {string.Join(", ", ex.Files)}; resolve manually", workspace.LeadPath, ct);
            return;
        }
        catch (InvalidOperationException ex)
        {
            await FinishAsync(team, item, false, $"child integration failed: {ex.Message}", workspace.LeadPath, ct);
            return;
        }
        var instruction = "All team members finished and their commits are now integrated into your branch. "
            + $"Their documents are staged (read-only copies) under {workspace.StagingPath}. Review and test the integrated code, "
            + "choose the canonical phase documents in the ticket folder, and commit every intended change before ending your turn.";
        var result = JobResult.Fail(JobErrors.DaemonUnhealthy);
        await Guard(item.Id, () =>
        {
            result = followUp?.Invoke(new FollowUpRequest(lead.JobId, instruction, $"prf-finalize:{item.Id:N}"))
                ?? JobResult.Fail(JobErrors.DaemonUnhealthy);
            if (result.Error is null) { teams.RecordMember(server, item.Id, "lead", FinalizeTurn, result.Job!.JobId); }
            return Task.CompletedTask;
        }, ct);
        if (result.Error is null)
        {
            onAccepted();
        }
        else if (result.Error is not (JobErrors.QueueFull or JobErrors.StorageBusy or JobErrors.StorageUnavailable or JobErrors.DaemonUnhealthy))
        {
            await FinishAsync(team, item, false, $"lead finalization could not resume: {result.Error}", workspace.LeadPath, ct);
        }
    }

    async Task<bool> AdvanceExternalAsync(PRFactoryWorkItem item, string[] names, CancellationToken ct)
    {
        var actor = externalTeam!;
        var owner = "prfactory:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{server}|{item.RepositoryId:D}|{item.Id:D}")));
        var existing = teams.ExternalMembers(server, item.Id);
        var teamId = existing.Count > 0 ? existing[0].TeamId : string.Empty;
        if (teamId.Length == 0)
        {
            await Guard(item.Id, () =>
            {
                teamId = actor.CreateActorTeam(owner) ?? throw new InvalidDataException("Cannot create PRFactory actor team");
                return Task.CompletedTask;
            }, ct);
        }
        foreach (var name in names)
        {
            var external = teams.External(server, item.Id, name);
            if (external is null)
            {
                await Guard(item.Id, () =>
                {
                    var ticket = actor.CreateTicketForTeam(teamId, name, $"PRFactory work item {item.Id:D}").Ticket
                        ?? throw new InvalidDataException("Cannot create external join ticket");
                    teams.RecordExternal(server, item.Id, name, ticket.Name, teamId, ticket.Token, ticket.ExpiresAt);
                    return Task.CompletedTask;
                }, ct);
                external = teams.External(server, item.Id, name)!;
            }
            if (!external.Closed)
            {
                await Guard(item.Id, () =>
                {
                    if (actor.RenewExpiredTicket(external.TeamId, external.ActualName) is { } renewed)
                    {
                        // An unjoined ticket expired; the fresh one appears in the private status snapshot.
                        teams.RenewExternal(server, item.Id, name, renewed.Token, renewed.ExpiresAt);
                    }
                    return Task.CompletedTask;
                }, ct);
            }
            if (!external.TicketUploaded && !external.Closed)
            {
                // The ticket is a bearer secret; everyone in the tenant can read the agent stream,
                // so publish only a notice. The owner reads the prompt via `atf prfactory status`.
                PRFactoryStreamResponse response = null!;
                await Guard(item.Id, async () => response = await client.UploadStreamAsync(item.Id, new PRFactoryStreamBatch(item.LeaseToken,
                    $"join:{item.Id:N}:{name}",
                    [new("member", teamId, name, "external", "Waiting", item.RepositoryId, "lead")],
                    [new(name, 1, DateTimeOffset.UtcNow, "Record",
                        $"External member {name} is waiting to join. On the connected machine run `atf prfactory status` for the private join prompt.",
                        "join-notice")]), ct), ct);
                if (!response.AcceptedThroughSeq.TryGetValue(name, out var seq) || seq < 1)
                {
                    throw new HttpRequestException("PRFactory did not acknowledge join ticket line");
                }

                teams.MarkTicketUploaded(server, item.Id, name);
            }
        }

        var externals = teams.ExternalMembers(server, item.Id);
        foreach (var external in externals.Where(e => !e.Closed && actor.HasLeft(e.TeamId, e.ActualName)))
        {
            teams.MarkExternalClosed(server, item.Id, external.Member);
        }

        var cursor = externals.Min(e => e.ReplySeq);
        var inbox = actor.ReadTeam(teamId, cursor, 50).Inbox;
        if (inbox is null && externals.All(e => e.Closed))
        {
            return true; // The team closed after its last upload; retry remote completion.
        }
        if (inbox is null)
        {
            throw new InvalidDataException("PRFactory actor team is unavailable");
        }
        var lines = inbox.Messages
            .Where(m => externals.Any(e => e.ActualName == m.From))
            .Select(m => new PRFactoryStreamLine(externals.First(e => e.ActualName == m.From).Member,
                m.Seq + 1, DateTimeOffset.Parse(m.CreatedAt), "Record", m.Text, "external-reply"))
            .ToList();
        if (lines.Count > 0)
        {
            PRFactoryStreamResponse response = null!;
            await Guard(item.Id, async () => response = await client.UploadStreamAsync(item.Id, new PRFactoryStreamBatch(item.LeaseToken,
                $"replies:{item.Id:N}:{cursor}:{inbox.NextSeq}", [], lines), ct), ct);
            if (lines.Any(line => !response.AcceptedThroughSeq.TryGetValue(line.AgentName, out var seq) || seq < line.Seq))
            {
                throw new HttpRequestException("PRFactory did not acknowledge external reply lines");
            }
        }
        foreach (var external in externals)
        {
            teams.SetReplySeq(server, item.Id, external.Member, inbox.NextSeq);
        }
        return !inbox.HasMore;
    }

    async Task FinishAsync(PRFactoryTeamRecord team, PRFactoryWorkItem item, bool success, string error,
        string? cwd, CancellationToken ct, string? result = null)
    {
        var leadId = teams.ManagedMembers(server, item.Id).LastOrDefault(m => m.Member == "lead")?.JobId;
        var job = leadId is null ? null : getJob(leadId);
        cwd = job is null ? cwd : JobWorktree.WorkingDirectory(job) ?? cwd;
        var workspace = workspaces?.Get(WorkspaceKey(item.Id));
        PublicationReceipt? receipt = null;
        List<PRFactoryRepositoryFreshnessRequest>? repositoryResults = null;
        if (success && teams.ArtefactDelivery(server, item.Id)?.Failure is null)
        {
            // Push the frozen lead head and verify the remote before any artefact upload or completion;
            // a retry after a lost response re-verifies the same intent instead of producing another commit.
            try
            {
                if (repositorySets?.Get(WorkspaceKey(item.Id)) is { } set)
                {
                    if (!await client.SupportsMultiRepoAsync(ct))
                    {
                        throw new HttpRequestException("PRFactory multi-repo-v1 capability disappeared during accepted work.");
                    }
                    (receipt, repositoryResults) = await PublishSetAsync(team, item, set, ct);
                }
                else { receipt = await PublishAsync(team, item, workspace, ct); }
            }
            catch (InvalidOperationException ex)
            {
                (success, error) = (false, $"Publication of {workspace?.PublishBranch} failed: {ex.Message} Local output retained in {workspace?.LeadPath}");
            }
        }
        var delivery = teams.ArtefactDelivery(server, item.Id);
        if (delivery is null)
        {
            string? payload = null;
            if (!success && leadId is not null)
            {
                error += $". Lead job: {leadId}; worktree: {cwd ?? "none"}; inspect local job results and logs";
            }
            if (success)
            {
                try
                {
                    var planRepositories = repositorySets?.Get(WorkspaceKey(item.Id)) is { } planSet
                        ? planSet.Members.Select(entry => (Guid.Parse(entry.Id), entry.Name,
                            workspaces!.Get(entry.WorkspaceKey)!.LeadPath)).ToArray() : null;
                    var artefacts = await PRFactoryArtefacts.CollectAsync(item,
                        cwd ?? throw new InvalidDataException("Missing lead worktree"), result, ct, planRepositories);
                    payload = JsonSerializer.Serialize(new PRFactoryArtefactRequest(artefacts, item.LeaseToken), PRFactoryWorkItemJson.Default.PRFactoryArtefactRequest);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                {
                    success = false;
                    error = $"Cannot collect {item.Type ?? "work item"} artefacts: {ex.Message}. Lead job: {leadId ?? "none"}; worktree: {cwd ?? "none"}";
                }
            }
            teams.FreezeArtefacts(server, item.Id, payload, success ? null : error);
            delivery = teams.ArtefactDelivery(server, item.Id)!;
        }
        if (delivery.Failure is { } failure) { (success, error) = (false, failure); }
        if (success && !team.Uploaded)
        {
            try
            {
                await Guard(item.Id, () => client.UploadArtefactPayloadAsync(item.Id, delivery.Payload!, ct), ct);
                teams.SetUploaded(server, item.Id);
            }
            catch (InvalidDataException ex)
            {
                (success, error) = (false, ex.Message);
                teams.FreezeArtefacts(server, item.Id, null, error);
            }
        }
        var pendingBlobs = success ? teams.PendingAttachments(server, item.Id) : null;
        var supportsBlobs = success && await client.SupportsBlobsAsync(ct);
        if (pendingBlobs is { Count: > 0 } && !supportsBlobs)
        {
            throw new HttpRequestException("Server blob capability unavailable while frozen attachments are pending");
        }
        if (supportsBlobs)
        {
            try
            {
                var pending = pendingBlobs;
                if (pending is null)
                {
                    var uploads = await PRFactoryAttachments.CollectAsync(team, item,
                        cwd ?? throw new InvalidDataException("Missing attachment workspace"), receipt, ct);
                    teams.FreezeAttachments(server, item.Id, uploads);
                    pending = teams.PendingAttachments(server, item.Id)!;
                }
                foreach (var upload in pending)
                {
                    await Guard(item.Id, () => client.UploadAttachmentAsync(item.Id, upload, ct), ct);
                    teams.AttachmentUploaded(server, item.Id, upload.ClientKey);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                (success, error) = (false, $"Cannot upload attachments: {ex.Message}");
                teams.FreezeArtefacts(server, item.Id, null, error);
            }
        }
        if (success)
        {
            // The server treats resultMarkdown as a replacement document for several phases.
            // Built-ins already uploaded their documents; never overwrite those with CLI chatter.
            if (item.Type == "CustomStep")
            {
                result = JsonSerializer.Deserialize(delivery.Payload!, PRFactoryWorkItemJson.Default.PRFactoryArtefactRequest)!
                    .Artefacts.Single().Content;
            }
            else if (item.Type is not (null or "Implementation" or "HostingNeedsDerivation" or "HostingResearch"))
            {
                result = null;
            }
            // Owned workspaces report only the verified public branch; the internal atf/team branch never leaves.
            var (branch, commit) = receipt is not null ? (receipt.Intent.PublishBranch, receipt.Intent.HeadSha)
                : item.RepositoryId is null || workspace is not null || item.ReadOnly || cwd is null ? (null, null) : (JobWorktree.Branch(cwd), JobWorktree.Head(cwd));
            var publication = receipt is null ? null : new PRFactoryRemotePublication(true, branch!, commit!, true);
            await Guard(item.Id, () => client.CompleteAsync(item.Id, item.LeaseToken, result, ct, branch, commit, publication,
                repositoryResults), ct);
            StopManagedJobs(item.Id);
            teams.Finish(server, item.Id, "completed");
            await Observe(item.Id, "completed", null, ct); // Also closes retained interactive sessions.
        }
        else
        {
            await Guard(item.Id, () => client.FailAsync(item.Id, item.LeaseToken, error, ct,
                error.StartsWith("agent_rate_limited", StringComparison.Ordinal)), ct);
            StopManagedJobs(item.Id);
            teams.Finish(server, item.Id, error.StartsWith("multi-repository", StringComparison.Ordinal) ? "refused" : "failed");
            await Observe(item.Id, "completed", "failure_reported", ct);
        }
    }

    void StopManagedJobs(Guid itemId)
    {
        foreach (var jobId in teams.MemberJobs(server, itemId))
        {
            if (getJob(jobId)?.Status is not (JobStatus.Queued or JobStatus.Running))
            {
                continue;
            }

            var stopped = stopJob?.Invoke(jobId) ?? throw new InvalidOperationException("PRFactory job stop is unavailable");
            if (stopped.Error is not null)
            {
                throw new InvalidOperationException($"PRFactory job stop failed: {stopped.Error}");
            }
        }
    }

    static string? MapBackend(PRFactoryAgentType agent) => agent switch
    {
        PRFactoryAgentType.ClaudeCode => "claude",
        PRFactoryAgentType.Codex => "codex",
        PRFactoryAgentType.PiAgent => "pi",
        PRFactoryAgentType.CursorCli => "cursor",
        PRFactoryAgentType.Droid => "droid",
        _ => null,
    };

    sealed class PRFactoryJobSubmissionException(string message) : Exception(message);
    sealed class PRFactoryFencedException(Guid id) : Exception($"PRFactory work item {id:D} is not under confirmed authority");
}
