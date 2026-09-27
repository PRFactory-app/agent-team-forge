using System.Text.Json;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Host.Features.PRFactory;

public sealed partial class PRFactoryWorkItems
{
    async Task AdvanceCommandsAsync(PRFactoryWorkItem item, CancellationToken ct)
    {
        if (item.LeaseToken is not Guid lease) { return; }
        foreach (var command in await client.DrainCommandsAsync(item.Id, lease, ct))
        {
            teams.SavePendingCommand(server, item.Id, command.CommandId,
                JsonSerializer.Serialize(command, PRFactoryWorkItemJson.Default.PRFactoryCommand),
                teams.ManagedMembers(server, item.Id).LastOrDefault(m => m.Member == command.TargetAgentName)?.JobId);
        }
        // Stop first, including commands drained on an earlier tick while a member was unjoined.
        var pending = teams.PendingCommands(server, item.Id)
            .Select(p => (Row: p, Command: JsonSerializer.Deserialize(p.Payload, PRFactoryWorkItemJson.Default.PRFactoryCommand)!))
            .OrderByDescending(p => p.Command.Kind.Equals("KillAgent", StringComparison.OrdinalIgnoreCase));
        var acks = new List<PRFactoryCommandAck>();
        foreach (var (row, command) in pending)
        {
            var receipt = teams.CommandReceipt(server, item.Id, command.CommandId);
            if (receipt is null)
            {
                var external = teams.External(server, item.Id, command.TargetAgentName);
                var kill = command.Kind.Equals("KillAgent", StringComparison.OrdinalIgnoreCase);
                var send = command.Kind.Equals("SendMessage", StringComparison.OrdinalIgnoreCase);
                if (!kill && !send) { receipt = new(false, "unknown_kind"); }
                else if (external is not null)
                {
                    if (kill)
                    {
                        var revoked = externalTeam!.RevokeMember(external.TeamId, external.ActualName);
                        receipt = new(revoked, revoked ? null : "member_not_found");
                        if (revoked) { teams.MarkExternalClosed(server, item.Id, external.Member); }
                    }
                    else if (external.Closed) { receipt = new(false, "member_closed"); }
                    else
                    {
                        AgentTeamForge.Business.Features.External.ExternalResult sent = null!;
                        await Guard(item.Id, () =>
                        {
                            sent = externalTeam!.SendToMemberOnce(external.TeamId, external.ActualName, command.Text, "prfactory", command.CommandId.ToString("D"));
                            return Task.CompletedTask;
                        }, ct);
                        if (sent.Error == "member_not_found" && !externalTeam!.HasLeft(external.TeamId, external.ActualName)) { continue; }
                        receipt = new(sent.Ok, sent.Error == "member_not_found" ? "member_left" : sent.Error);
                    }
                }
                else if (row.ParentJob is { } parent)
                {
                    JobResult outcome;
                    if (kill)
                    {
                        // Stop every turn, including a deferred follow-up accepted before this command.
                        // Mark first: an idle (completed) member has nothing to cancel, yet must stay closed.
                        teams.MarkManagedKilled(server, item.Id, command.TargetAgentName);
                        outcome = JobResult.Fail("member_not_found");
                        foreach (var member in teams.ManagedMembers(server, item.Id).Where(m => m.Member == command.TargetAgentName).Reverse())
                        {
                            outcome = stopJob?.Invoke(member.JobId) ?? JobResult.Fail(JobErrors.DaemonUnhealthy);
                            if (outcome.Error is not null) { break; }
                        }
                    }
                    else if (teams.IsManagedKilled(server, item.Id, command.TargetAgentName) || getJob(parent)?.Status == JobStatus.Cancelled)
                    {
                        outcome = JobResult.Fail("member_closed");
                    }
                    else
                    {
                        var accepted = JobResult.Fail(JobErrors.DaemonUnhealthy);
                        await Guard(item.Id, () =>
                        {
                            accepted = followUp?.Invoke(new FollowUpRequest(parent, command.Text ?? "", "prf-command:" + command.CommandId.ToString("N")) { Defer = true })
                                ?? JobResult.Fail(JobErrors.DaemonUnhealthy);
                            if (accepted.Error is null)
                            {
                                var members = teams.ManagedMembers(server, item.Id).Where(m => m.Member == command.TargetAgentName).ToList();
                                if (!members.Any(m => m.JobId == accepted.Job!.JobId))
                                {
                                    teams.RecordMember(server, item.Id, command.TargetAgentName, members.Max(m => m.Turn) + 1, accepted.Job!.JobId);
                                }
                            }
                            return Task.CompletedTask;
                        }, ct);
                        outcome = accepted;
                        if (outcome.Error is null) { onAccepted(); }
                    }
                    if (outcome.Error is JobErrors.QueueFull or JobErrors.StorageBusy or JobErrors.StorageUnavailable or JobErrors.DaemonUnhealthy or JobErrors.ParentNotReady) { continue; }
                    receipt = new(outcome.Error is null, outcome.Error);
                }
                else { receipt = new(false, "member_not_found"); }
                teams.RecordCommand(server, item.Id, command.CommandId, receipt.Accepted, receipt.Reason);
            }
            acks.Add(new(command.CommandId, receipt.Accepted, receipt.Reason));
        }
        if (acks.Count > 0)
        {
            await Guard(item.Id, () => client.AckCommandsAsync(item.Id, lease, acks, ct), ct);
            foreach (var ack in acks) { teams.RemovePendingCommand(server, item.Id, ack.CommandId); }
        }
    }

    async Task<bool> UploadManagedAsync(PRFactoryWorkItem item, CancellationToken ct)
    {
        var drained = true;
        foreach (var group in teams.ManagedMembers(server, item.Id).GroupBy(m => m.Member))
        {
            long previousSeq = 0;
            foreach (var member in group)
            {
                var job = getJob(member.JobId)!;
                var position = teams.StreamPosition(server, item.Id, job.JobId);
                if (position.Pending is null)
                {
                    var output = jobLogs?.Read(job.JobId, position.Offset, 8192);
                    var seq = Math.Max(previousSeq, position.Seq);
                    var lines = new List<PRFactoryStreamLine>();
                    void Add(string text, string kind)
                    {
                        for (var i = 0; i < text.Length; i += 2048)
                        {
                            lines.Add(new(member.Member, ++seq, DateTimeOffset.UtcNow, "Record", text.Substring(i, Math.Min(2048, text.Length - i)), kind));
                        }
                    }
                    if (output?.Text is { Length: > 0 } text) { Add(text, "job-output"); }
                    // Publish terminal status/result only after the final output page.
                    var caughtUp = output is null || output.NextOffset >= output.EndOffset;
                    var resultOffset = position.ResultOffset;
                    if (caughtUp && job.ResultText is { } result && resultOffset < result.Length)
                    {
                        var count = Math.Min(8192, result.Length - resultOffset);
                        Add(result.Substring(resultOffset, count), "job-result");
                        resultOffset += count;
                        caughtUp = resultOffset == result.Length;
                    }
                    if (caughtUp && position.Status != job.Status)
                    {
                        Add(job.Status + (job.ReasonCode is null ? "" : ": " + job.ReasonCode), "job-status");
                    }
                    if (lines.Count > 0)
                    {
                        var state = job.Status switch
                        {
                            JobStatus.Completed => "Done",
                            JobStatus.Cancelled => "Killed",
                            JobStatus.Failed => "Failed",
                            JobStatus.Running => "Running",
                            JobStatus.NeedsReconciliation => "Waiting",
                            _ => "Starting"
                        };
                        var batch = new PRFactoryStreamBatch(item.LeaseToken, $"managed:{job.JobId}:{seq}",
                            [new("member", item.Id.ToString("D"), member.Member, job.Backend, state, item.RepositoryId, member.Member == "lead" ? null : "lead")], lines);
                        position = new(output?.NextOffset ?? position.Offset, seq, caughtUp ? job.Status : position.Status,
                            JsonSerializer.Serialize(batch, PRFactoryWorkItemJson.Default.PRFactoryStreamBatch), resultOffset);
                        // Persist exact bytes and positions before HTTP; a lost response replays the same batch.
                        teams.SaveStreamPosition(server, item.Id, job.JobId, position);
                    }
                    if (!caughtUp) { drained = false; }
                }
                if (position.Pending is not null)
                {
                    var batch = JsonSerializer.Deserialize(position.Pending, PRFactoryWorkItemJson.Default.PRFactoryStreamBatch)!;
                    PRFactoryStreamResponse response = null!;
                    await Guard(item.Id, async () => response = await client.UploadStreamAsync(item.Id, batch, ct), ct);
                    if (!response.AcceptedThroughSeq.TryGetValue(member.Member, out var accepted) || accepted < position.Seq)
                    {
                        throw new HttpRequestException("PRFactory did not acknowledge managed output");
                    }
                    position = position with { Pending = null };
                    teams.SaveStreamPosition(server, item.Id, job.JobId, position);
                }
                previousSeq = position.Seq;
                if (position.Status != job.Status || jobLogs?.Read(job.JobId, position.Offset, 1) is { Text.Length: > 0 }) { drained = false; break; }
                // A deferred turn must not allocate sequence numbers ahead of its still-running parent.
                if (job.Status is JobStatus.Queued or JobStatus.Running or JobStatus.NeedsReconciliation) { break; }
            }
        }
        return drained;
    }
}
