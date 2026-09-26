using System.Text.Json;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Host.Features.PRFactory;

/// <summary>Polls claims and resumes durable local teams. Remote acceptance/reconciliation is a separate server slice.</summary>
public sealed class PRFactoryWorkItems(
    string server, IReadOnlyList<RepositoryMapping> repositories, PRFactoryTeamStore teams,
    PRFactoryClient client, Func<SubmitJobRequest, JobResult> submit, Func<string, JobRecord?> getJob,
    Action onAccepted)
{
    public async Task TickAsync(Guid? machineId, CancellationToken ct)
    {
        foreach (var pending in teams.Pending(server)) await AdvanceAsync(pending, ct);
        var offered = await client.PollAsync(repositories.Select(r => r.Id), machineId, ct);
        foreach (var item in offered)
        {
            if (item.Id == Guid.Empty || teams.Get(server, item.Id) is not null) continue;
            var claimed = await client.ClaimAsync(item.Id, machineId, ct);
            if (claimed is null || claimed.Id != item.Id) continue;
            var json = JsonSerializer.Serialize(claimed, PRFactoryWorkItemJson.Default.PRFactoryWorkItem);
            teams.CreateIfAbsent(server, claimed.Id, json); // Commit before the first submit.
            await AdvanceAsync(teams.Get(server, claimed.Id)!, ct);
        }
    }

    async Task AdvanceAsync(PRFactoryTeamRecord team, CancellationToken ct)
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
        if (members.Any(m => m.Name == "lead") || members.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count() != members.Length
            || members.Any(m => string.IsNullOrWhiteSpace(m.Name) || m.MaxIterations is < 1)
            || (plan is not null && (plan.MaxConcurrentChildren < 0 || plan.FreeRoomCeiling < 0)))
        {
            await FinishAsync(team, item, false, "invalid team recipe", repo.Directory, ct);
            return;
        }
        if (MapBackend(item.AgentType) is null || members.Any(m => MapBackend(m.Backend ?? item.AgentType) is null))
        {
            await FinishAsync(team, item, false, "unsupported agent backend", repo.Directory, ct);
            return;
        }
        // The recipe's free room is reserved for later command-driven turns. This slice submits
        // only declared members. MaxIterations is persisted in the claim and gates later turns.
        var maxChildren = plan is null ? 0 : Math.Min(plan.MaxConcurrentChildren, members.Length);
        if (members.Length > 0 && maxChildren == 0)
        {
            await FinishAsync(team, item, false, "team recipe permits no concurrent children", repo.Directory, ct);
            return;
        }
        var lead = await SubmitMemberAsync(team, item, "lead", item.AgentType, item.Model, item.Effort, item.Prompt, repo.Directory, ct);
        if (lead is null) return;
        var active = 0;
        var allJobs = new List<JobRecord> { lead };
        foreach (var member in members)
        {
            var mapped = teams.MemberJob(server, item.Id, member.Name, 0);
            if (mapped is not null)
            {
                var existing = getJob(mapped);
                if (existing is null) throw new InvalidDataException("Persisted PRFactory job is missing");
                allJobs.Add(existing);
                if (existing.Status is JobStatus.Queued or JobStatus.Running) active++;
                continue;
            }
            if (active >= maxChildren) continue;
            var instruction = $"{item.Prompt}\n\nRole: {member.Role}\nMember: {member.Name}"
                + (string.IsNullOrWhiteSpace(member.Notes) ? "" : $"\nNotes: {member.Notes}");
            var child = await SubmitMemberAsync(team, item, member.Name, member.Backend ?? item.AgentType,
                member.Model ?? item.Model, member.Effort ?? item.Effort, instruction, repo.Directory, ct);
            if (child is null) return;
            allJobs.Add(child);
            if (child.Status is JobStatus.Queued or JobStatus.Running) active++;
        }
        if (allJobs.Count != members.Length + 1 || allJobs.Any(j => j.Status is JobStatus.Queued or JobStatus.Running)) return;
        var failed = allJobs.FirstOrDefault(j => j.Status != JobStatus.Completed);
        await FinishAsync(team, item, failed is null, failed?.ReasonCode ?? "job failed", repo.Directory, ct, lead.ResultText);
    }

    Task<JobRecord?> SubmitMemberAsync(PRFactoryTeamRecord team, PRFactoryWorkItem item, string member,
        PRFactoryAgentType agent, string? model, PRFactoryEffort? effort, string instruction, string cwd, CancellationToken ct)
    {
        var existingId = teams.MemberJob(server, item.Id, member, 0);
        if (existingId is not null) return Task.FromResult(getJob(existingId));
        var backend = MapBackend(agent);
        if (backend is null) throw new InvalidDataException($"Unsupported PRFactory backend: {agent}");
        // The same key and exact request resolve a lost local acceptance response to one job.
        var prefix = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(server)))[..12];
        var memberKey = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(member)))[..12];
        var key = $"prf:{prefix}:{item.Id:N}:{memberKey}:0";
        var result = submit(new SubmitJobRequest(key, instruction, null, false)
        {
            Backend = backend, Cwd = cwd, Model = model, Effort = effort?.ToString().ToLowerInvariant(),
        });
        if (result.Error is not null) throw new InvalidOperationException($"PRFactory job submit: {result.Error}");
        var jobId = result.Job!.JobId;
        teams.RecordMember(server, item.Id, member, 0, jobId);
        if (result.Outcome == "accepted") onAccepted();
        return Task.FromResult(getJob(jobId));
    }

    async Task FinishAsync(PRFactoryTeamRecord team, PRFactoryWorkItem item, bool success, string error,
        string? cwd, CancellationToken ct, string? result = null)
    {
        if (!team.Uploaded)
        {
            var artefacts = new List<PRFactoryArtefactFile>();
            if (cwd is not null && item.ExpectedOutput is { Length: > 0 } output)
            {
                var folder = item.TicketArtefactFolder ?? string.Empty;
                var root = Path.GetFullPath(cwd);
                var file = Path.GetFullPath(Path.Combine(root, folder, output));
                if (Path.GetFileName(output) != output || !file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException("PRFactory artefact path escapes repository");
                if (File.Exists(file) && new FileInfo(file).LinkTarget is null)
                    artefacts.Add(new PRFactoryArtefactFile(output, await File.ReadAllTextAsync(file, ct), null));
            }
            await client.UploadArtefactsAsync(item.Id, item.LeaseToken, artefacts, ct);
            teams.SetUploaded(server, item.Id);
        }
        if (success)
        {
            await client.CompleteAsync(item.Id, item.LeaseToken, result, ct);
            teams.Finish(server, item.Id, "completed");
        }
        else
        {
            await client.FailAsync(item.Id, item.LeaseToken, error, ct);
            teams.Finish(server, item.Id, error.StartsWith("multi-repository", StringComparison.Ordinal) ? "refused" : "failed");
        }
    }

    static bool HasSecondaries(PRFactoryWorkItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ContextJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(item.ContextJson);
            return document.RootElement.TryGetProperty("repositories", out var repositories)
                && repositories.TryGetProperty("secondary", out var secondary)
                && secondary.ValueKind == JsonValueKind.Array && secondary.GetArrayLength() > 0;
        }
        catch (JsonException) { return false; }
    }

    static string? MapBackend(PRFactoryAgentType agent) => agent switch
    {
        PRFactoryAgentType.ClaudeCode => "claude",
        PRFactoryAgentType.Codex => "codex",
        PRFactoryAgentType.PiAgent => "pi",
        _ => null,
    };
}
