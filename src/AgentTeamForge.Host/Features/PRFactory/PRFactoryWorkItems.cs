using System.Text.Json;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.External;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Host.Features.PRFactory;

/// <summary>Polls claims and reconciles server ownership before dispatch or publication.</summary>
public sealed class PRFactoryWorkItems(
    string server, IReadOnlyList<RepositoryMapping> repositories, PRFactoryTeamStore teams,
    PRFactoryClient client, Func<SubmitJobRequest, JobResult> submit, Func<string, JobRecord?> getJob,
    Action onAccepted, Func<string, string?>? leadSessionFor = null, Action<string>? log = null,
    ExternalTeam? externalTeam = null, Func<string, JobResult>? stopJob = null)
{
    public async Task TickAsync(Guid? machineId, CancellationToken ct)
    {
        foreach (var pending in teams.Pending(server))
        {
            await IsolateAsync(pending.WorkItemId, () => AdvanceAsync(pending, ct), ct);
        }

        var offered = await client.PollAsync(repositories.Select(r => r.Id), machineId, ct);
        foreach (var item in offered)
        {
            if (item.Id == Guid.Empty || teams.Get(server, item.Id) is not null)
            {
                continue;
            }

            var claimed = await client.ClaimAsync(item.Id, machineId, ct);
            if (claimed is null || claimed.Id != item.Id)
            {
                continue;
            }

            var json = JsonSerializer.Serialize(claimed, PRFactoryWorkItemJson.Default.PRFactoryWorkItem);
            teams.CreateIfAbsent(server, claimed.Id, json, machineId); // Commit identity before POST or dispatch.
            await IsolateAsync(claimed.Id, () => AdvanceAsync(teams.Get(server, claimed.Id)!, ct), ct);
        }
    }

    // One failing team must not block every other team and new claims; it retries next tick.
    async Task IsolateAsync(Guid id, Func<Task> advance, CancellationToken ct)
    {
        try
        {
            await advance();
        }
        catch (Exception ex) when (ex is not (WorkerTokenRejectedException or OutOfMemoryException)
            && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            log?.Invoke($"PRFactory work item {id:D} deferred ({ex.GetType().Name})");
        }
    }

    async Task AdvanceAsync(PRFactoryTeamRecord team, CancellationToken ct)
    {
        try
        {
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
                Fence(team.WorkItemId);
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
    }

    async Task<bool> ConfirmAcceptanceAsync(PRFactoryTeamRecord team, CancellationToken ct)
    {
        if (team.AcceptanceState == "reconciliation_needed")
        {
            return false;
        }
        if (team.AcceptanceState == "legacy")
        {
            return true;
        }
        if (team.MachineId is not Guid machine || team.AtfJobId is not { Length: > 0 } jobId)
        {
            throw new InvalidDataException("PRFactory acceptance identity is missing");
        }

        if (team.AcceptanceState == "accepted")
        {
            var observed = await client.GetAtfAcceptanceAsync(team.WorkItemId, machine, jobId, ct);
            if (observed != PRFactoryClient.AcceptanceResult.Confirmed)
            {
                Fence(team.WorkItemId);
                return false;
            }
            return true;
        }

        var item = JsonSerializer.Deserialize(team.ClaimedJson, PRFactoryWorkItemJson.Default.PRFactoryWorkItem)
            ?? throw new InvalidDataException("Invalid persisted PRFactory claim");
        if (item.LeaseToken is not Guid lease || lease == Guid.Empty)
        {
            Fence(team.WorkItemId);
            return false;
        }
        var accepted = await client.AcceptAtfAsync(team.WorkItemId, machine, lease, jobId, ct);
        if (accepted == PRFactoryClient.AcceptanceResult.Conflict)
        {
            Fence(team.WorkItemId);
            return false;
        }
        if (accepted == PRFactoryClient.AcceptanceResult.NotFound)
        {
            var observed = await client.GetAtfAcceptanceAsync(team.WorkItemId, machine, jobId, ct);
            if (observed == PRFactoryClient.AcceptanceResult.Conflict)
            {
                Fence(team.WorkItemId);
                return false;
            }
            if (observed == PRFactoryClient.AcceptanceResult.Confirmed)
            {
                teams.SetAcceptance(server, team.WorkItemId, "accepted");
                return true;
            }
            // Both acceptance routes are absent or the item disappeared. The established lease
            // heartbeat distinguishes a missing feature from a lost claim before dispatch.
            if (!await client.ConfirmLegacyLeaseAsync(team.WorkItemId, lease, ct))
            {
                Fence(team.WorkItemId);
                return false;
            }
            teams.SetAcceptance(server, team.WorkItemId, "legacy");
            client.LogLegacyOnce(log);
            return true;
        }
        teams.SetAcceptance(server, team.WorkItemId, "accepted");
        return true;
    }

    void Fence(Guid id)
    {
        teams.SetAcceptance(server, id, "reconciliation_needed");
        log?.Invoke($"PRFactory work item {id:D} reconciliation needed; remote publication fenced");
    }

    async Task AdvanceCoreAsync(PRFactoryTeamRecord team, CancellationToken ct)
    {
        var item = JsonSerializer.Deserialize(team.ClaimedJson, PRFactoryWorkItemJson.Default.PRFactoryWorkItem)
            ?? throw new InvalidDataException("Invalid persisted PRFactory claim");
        if (HasSecondaries(item))
        {
            await FinishAsync(team, item, false, "multi-repository work items are unsupported", null, ct);
            return;
        }
        var repo = repositories.SingleOrDefault(r => r.Id == item.RepositoryId);
        if (repo is null)
        {
            await FinishAsync(team, item, false, "repository has no approved local mapping", null, ct);
            return;
        }
        var plan = item.TeamPlan;
        var members = (plan?.Members ?? []).Where(m => !m.IsLead).OrderBy(m => m.Order).ToArray();
        var externalNames = repo.ExternalMembers ?? [];
        if (members.Any(m => m.Name == "lead") || members.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count() != members.Length
            || members.Any(m => string.IsNullOrWhiteSpace(m.Name) || m.MaxIterations is < 1)
            || (plan is not null && (plan.MaxConcurrentChildren < 0 || plan.FreeRoomCeiling < 0)))
        {
            await FinishAsync(team, item, false, "invalid team recipe", repo.Directory, ct);
            return;
        }
        if (MapBackend(item.AgentType) is null || members.Any(m => !externalNames.Contains(m.Name, StringComparer.Ordinal)
            && MapBackend(m.Backend ?? item.AgentType) is null)
            || externalNames.Any(name => !members.Any(m => m.Name == name))
            || (externalNames.Length > 0 && externalTeam is null))
        {
            await FinishAsync(team, item, false, "unsupported agent backend", repo.Directory, ct);
            return;
        }
        // The recipe's free room is reserved for later command-driven turns. This slice submits
        // only declared members. MaxIterations is persisted in the claim and gates later turns.
        var managedMembers = members.Where(m => !externalNames.Contains(m.Name, StringComparer.Ordinal)).ToArray();
        var maxChildren = plan is null ? 0 : Math.Min(plan.MaxConcurrentChildren, managedMembers.Length);
        if (managedMembers.Length > 0 && maxChildren == 0)
        {
            await FinishAsync(team, item, false, "team recipe permits no concurrent children", repo.Directory, ct);
            return;
        }
        JobRecord? lead;
        try
        {
            lead = SubmitMember(item, "lead", item.AgentType, item.Model, item.Effort, item.Prompt, repo.Directory);
        }
        catch (PRFactoryJobSubmissionException ex)
        {
            await FinishAsync(team, item, false, ex.Message, repo.Directory, ct);
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

            var instruction = $"{item.Prompt}\n\nRole: {member.Role}\nMember: {member.Name}"
                + (string.IsNullOrWhiteSpace(member.Notes) ? "" : $"\nNotes: {member.Notes}");
            JobRecord? child;
            try
            {
                child = SubmitMember(item, member.Name, member.Backend ?? item.AgentType,
                    member.Model ?? item.Model, member.Effort ?? item.Effort, instruction, repo.Directory);
            }
            catch (PRFactoryJobSubmissionException ex)
            {
                await FinishAsync(team, item, false, ex.Message, repo.Directory, ct);
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
        var waitForManaged = managedMembers.Length > 0 || externalNames.Length == 0;
        if (allJobs.Count != managedMembers.Length + 1 || !externalRepliesDrained
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
            externalTeam!.CloseTeam(externals[0].TeamId);
            teams.MarkExternalClosed(server, item.Id);
        }
        var failed = allJobs.FirstOrDefault(j => j.Status != JobStatus.Completed);
        await FinishAsync(team, item, failed is null || !waitForManaged && lead.Status is JobStatus.Queued or JobStatus.Running or JobStatus.NeedsReconciliation,
            failed?.ReasonCode ?? "job failed", repo.Directory, ct,
            lead.ResultText ?? (!waitForManaged ? "External members completed their work; replies are in the agent stream." : null));
    }

    JobRecord? SubmitMember(PRFactoryWorkItem item, string member,
        PRFactoryAgentType agent, string? model, PRFactoryEffort? effort, string instruction, string cwd)
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
        var result = submit(new SubmitJobRequest(key, instruction, null, false)
        {
            Backend = backend,
            Cwd = cwd,
            Model = model,
            Effort = effort?.ToString().ToLowerInvariant(),
            LeadSessionId = leadSessionFor?.Invoke(cwd),
        });
        if (result.Error is JobErrors.QueueFull or JobErrors.StorageBusy or JobErrors.DaemonUnhealthy)
        {
            return null;
        }
        if (result.Error is not null)
        {
            throw new PRFactoryJobSubmissionException($"PRFactory job submit: {result.Error}");
        }

        var jobId = result.Job!.JobId;
        teams.RecordMember(server, item.Id, member, 0, jobId);
        if (result.Outcome == "accepted")
        {
            onAccepted();
        }

        return getJob(jobId);
    }

    async Task<bool> AdvanceExternalAsync(PRFactoryWorkItem item, string[] names, CancellationToken ct)
    {
        var actor = externalTeam!;
        var owner = "prfactory:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{server}|{item.RepositoryId:D}|{item.Id:D}")));
        var existing = teams.ExternalMembers(server, item.Id);
        var teamId = (existing.Count > 0 ? existing[0].TeamId : null) ?? actor.CreateActorTeam(owner)
            ?? throw new InvalidDataException("Cannot create PRFactory actor team");
        foreach (var name in names)
        {
            var external = teams.External(server, item.Id, name);
            if (external is null)
            {
                var ticket = actor.CreateTicketForTeam(teamId, name, $"PRFactory work item {item.Id:D}").Ticket
                    ?? throw new InvalidDataException("Cannot create external join ticket");
                teams.RecordExternal(server, item.Id, name, ticket.Name, teamId, ticket.Token, ticket.ExpiresAt);
                external = teams.External(server, item.Id, name)!;
            }
            if (!external.Closed && actor.RenewExpiredTicket(external.TeamId, external.ActualName) is { } renewed)
            {
                // An unjoined ticket expired; the fresh one appears in the private status snapshot.
                teams.RenewExternal(server, item.Id, name, renewed.Token, renewed.ExpiresAt);
            }
            if (!external.TicketUploaded && !external.Closed)
            {
                // The ticket is a bearer secret; everyone in the tenant can read the agent stream,
                // so publish only a notice. The owner reads the prompt via `atf prfactory status`.
                var response = await client.UploadStreamAsync(item.Id, new PRFactoryStreamBatch(item.LeaseToken,
                    $"join:{item.Id:N}:{name}",
                    [new("member", teamId, name, "external", "Waiting", item.RepositoryId, "lead")],
                    [new(name, 1, DateTimeOffset.UtcNow, "Record",
                        $"External member {name} is waiting to join. On the connected machine run `atf prfactory status` for the private join prompt.",
                        "join-notice")]), ct);
                if (!response.AcceptedThroughSeq.TryGetValue(name, out var seq) || seq < 1)
                {
                    throw new HttpRequestException("PRFactory did not acknowledge join ticket line");
                }

                teams.MarkTicketUploaded(server, item.Id, name);
            }
        }

        var lease = item.LeaseToken ?? throw new InvalidDataException("PRFactory external work requires a lease token");
        var acks = new List<PRFactoryCommandAck>();
        foreach (var command in await client.DrainCommandsAsync(item.Id, lease, ct))
        {
            if (!names.Contains(command.TargetAgentName, StringComparer.Ordinal))
            {
                continue;
            }

            var receipt = teams.CommandReceipt(server, item.Id, command.CommandId);
            if (receipt is null)
            {
                if (command.Kind.Equals("SendMessage", StringComparison.OrdinalIgnoreCase))
                {
                    var target = teams.External(server, item.Id, command.TargetAgentName)!;
                    if (target.Closed)
                    {
                        receipt = new(false, "member_closed");
                    }
                    else
                    {
                        var sent = actor.SendToMemberOnce(target.TeamId, target.ActualName, command.Text, "prfactory", command.CommandId.ToString("D"));
                        if (sent.Error == "member_not_found" && !actor.HasLeft(target.TeamId, target.ActualName))
                        {
                            continue; // Await the participant joining before acknowledging.
                        }

                        receipt = new(sent.Ok, sent.Error == "member_not_found" ? "member_left" : sent.Error);
                    }
                }
                else if (command.Kind.Equals("KillAgent", StringComparison.OrdinalIgnoreCase))
                {
                    var target = teams.External(server, item.Id, command.TargetAgentName)!;
                    receipt = new(actor.RevokeMember(target.TeamId, target.ActualName), null);
                    if (receipt.Accepted)
                    {
                        teams.MarkExternalClosed(server, item.Id, target.Member);
                    }
                }
                else
                {
                    receipt = new(false, "unknown_kind");
                }

                teams.RecordCommand(server, item.Id, command.CommandId, receipt.Accepted, receipt.Reason);
            }
            acks.Add(new(command.CommandId, receipt.Accepted, receipt.Reason));
        }
        if (acks.Count > 0)
        {
            await client.AckCommandsAsync(item.Id, lease, acks, ct);
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
            var response = await client.UploadStreamAsync(item.Id, new PRFactoryStreamBatch(item.LeaseToken,
                $"replies:{item.Id:N}:{cursor}:{inbox.NextSeq}", [], lines), ct);
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
        var file = cwd is not null && item.ExpectedOutput is { Length: > 0 } output
            ? ArtefactPath(cwd, item.TicketArtefactFolder ?? string.Empty, output) ?? string.Empty
            : null;
        if (file == string.Empty)
        {
            // A server-supplied path that escapes the mapping fails the item instead of retrying forever.
            (success, error, file) = (false, "artefact path is outside the mapped repository", null);
        }
        if (!team.Uploaded)
        {
            var artefacts = new List<PRFactoryArtefactFile>();
            if (file is not null && File.Exists(file))
            {
                artefacts.Add(new PRFactoryArtefactFile(item.ExpectedOutput!, await File.ReadAllTextAsync(file, ct), null));
            }
            await client.UploadArtefactsAsync(item.Id, item.LeaseToken, artefacts, ct);
            teams.SetUploaded(server, item.Id);
        }
        if (success)
        {
            await client.CompleteAsync(item.Id, item.LeaseToken, result, ct);
            StopManagedJobs(item.Id);
            teams.Finish(server, item.Id, "completed");
        }
        else
        {
            await client.FailAsync(item.Id, item.LeaseToken, error, ct);
            StopManagedJobs(item.Id);
            teams.Finish(server, item.Id, error.StartsWith("multi-repository", StringComparison.Ordinal) ? "refused" : "failed");
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

    static string? ArtefactPath(string cwd, string folder, string output)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
        var file = Path.GetFullPath(Path.Combine(root, folder, output));
        if (Path.GetFileName(output) != output || !file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        var current = root;
        foreach (var part in Path.GetRelativePath(root, Path.GetDirectoryName(file)!).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (new DirectoryInfo(current).LinkTarget is not null)
            {
                return null;
            }
        }
        return new FileInfo(file).LinkTarget is null ? file : null;
    }

    static bool HasSecondaries(PRFactoryWorkItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ContextJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(item.ContextJson);
            return document.RootElement.TryGetProperty("repositories", out var repositories)
                && repositories.TryGetProperty("secondary", out var secondary)
                && secondary.ValueKind == JsonValueKind.Array && secondary.GetArrayLength() > 0;
        }
        catch (JsonException) { return true; }
    }

    static string? MapBackend(PRFactoryAgentType agent) => agent switch
    {
        PRFactoryAgentType.ClaudeCode => "claude",
        PRFactoryAgentType.Codex => "codex",
        PRFactoryAgentType.PiAgent => "pi",
        _ => null,
    };

    sealed class PRFactoryJobSubmissionException(string message) : Exception(message);
}
