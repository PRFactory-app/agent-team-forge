using System.Net;
using System.Text.Json.Serialization;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Jobs.Publication;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Host.Features.PRFactory;

public sealed record PRFactoryWipReport(Guid LeaseToken, Guid MachineId, string AtfJobId,
    Guid RepositoryId, string BaseCommitSha, string BranchName, string HeadSha, int CommitCount,
    bool Succeeded, string? FailureMessage, string PublicationId);
public sealed record PRFactoryWipResponse(bool Accepted, string? ReceiptId, string? VerifiedHeadSha);
public sealed record PRFactoryReleaseRequest(string Reason, Guid LeaseToken, Guid MachineId, string AtfJobId,
    Guid RepositoryId, string BaseCommitSha, string ReleaseId, string WipBranchName, string VerifiedWipSha);
public sealed record PRFactoryReleaseResponse(bool Released, string? ReleaseId, string? VerifiedWipSha);
public sealed record PRFactoryBaseConflictRequest(Guid[] RepositoryIds, string[] ConflictingPaths, Guid LeaseToken);
public sealed record PRFactoryRepositoryFreshnessRequest(Guid LeaseToken, Guid RepositoryId, string BaseCommitSha,
    string BranchName, string? HeadCommitSha, int PushState, string? Message, string BaseBranchName,
    string CurrentBaseShaAtStart, int CommitsBehindAtStart, int RefreshAction);
public sealed record PRFactoryCapabilityResponse(string[] Capabilities);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PRFactoryWipReport))]
[JsonSerializable(typeof(PRFactoryWipResponse))]
[JsonSerializable(typeof(PRFactoryReleaseRequest))]
[JsonSerializable(typeof(PRFactoryReleaseResponse))]
[JsonSerializable(typeof(PRFactoryBaseConflictRequest))]
[JsonSerializable(typeof(PRFactoryRepositoryFreshnessRequest))]
[JsonSerializable(typeof(PRFactoryCapabilityResponse))]
internal sealed partial class PRFactoryBaseWipJson : JsonSerializerContext;

public sealed partial class PRFactoryClient
{
    public async Task<bool> SupportsMultiRepoAsync(CancellationToken ct)
    {
        using var response = await httpClient.GetAsync("api/worker/capabilities", ct);
        RejectToken(response.StatusCode);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            workerVersion = "1.0.0";
            return false;
        }
        response.EnsureSuccessStatusCode();
        var capabilities = await response.Content.ReadFromJsonAsync(PRFactoryBaseWipJson.Default.PRFactoryCapabilityResponse, ct);
        var supported = capabilities?.Capabilities?.Contains("multi-repo-v1", StringComparer.Ordinal) == true;
        workerVersion = supported ? "1.1.0" : "1.0.0";
        return supported;
    }

    public async Task<bool> SupportsBaseWipAsync(CancellationToken ct)
    {
        using var response = await httpClient.GetAsync("api/worker/capabilities", ct);
        RejectToken(response.StatusCode);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        var capabilities = await response.Content.ReadFromJsonAsync(PRFactoryBaseWipJson.Default.PRFactoryCapabilityResponse, ct);
        return capabilities?.Capabilities?.Contains("base-wip-v1", StringComparer.Ordinal) == true;
    }

    public async Task ReportBaseConflictAsync(Guid id, PRFactoryBaseConflictRequest request, CancellationToken ct)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"api/worker/work-items/{id:D}/base-conflict?workerVersion=1.0.0", request,
            PRFactoryBaseWipJson.Default.PRFactoryBaseConflictRequest, ct);
        RejectToken(response.StatusCode);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new PRFactoryLeaseLostException(id);
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task ReportFreshnessAsync(Guid id, PRFactoryRepositoryFreshnessRequest request, CancellationToken ct)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/worker/work-items/{id:D}/repository-result",
            request, PRFactoryBaseWipJson.Default.PRFactoryRepositoryFreshnessRequest, ct);
        RejectToken(response.StatusCode);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new PRFactoryLeaseLostException(id);
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task<string> ReportWipAsync(Guid id, PRFactoryWipReport report, CancellationToken ct)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/worker/work-items/{id:D}/wip-publication",
            report, PRFactoryBaseWipJson.Default.PRFactoryWipReport, ct);
        RejectToken(response.StatusCode);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new PRFactoryLeaseLostException(id);
        }

        response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync(PRFactoryBaseWipJson.Default.PRFactoryWipResponse, ct);
        if (receipt is not { Accepted: true, ReceiptId.Length: > 0 } || receipt.VerifiedHeadSha != report.HeadSha)
        {
            throw new InvalidDataException("Server did not acknowledge the verified WIP SHA.");
        }

        return receipt.ReceiptId;
    }

    public async Task ReportWipFailureAsync(Guid id, PRFactoryWipReport report, CancellationToken ct)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/worker/work-items/{id:D}/wip-publication",
            report, PRFactoryBaseWipJson.Default.PRFactoryWipReport, ct);
        RejectToken(response.StatusCode);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new PRFactoryLeaseLostException(id);
        }
        response.EnsureSuccessStatusCode();
    }

    public async Task<PRFactoryReleaseResponse> ReleaseWipAsync(Guid id, PRFactoryReleaseRequest request, CancellationToken ct)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/worker/release/{id:D}", request,
            PRFactoryBaseWipJson.Default.PRFactoryReleaseRequest, ct);
        RejectToken(response.StatusCode);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new PRFactoryLeaseLostException(id);
        }

        response.EnsureSuccessStatusCode();
        var ack = await response.Content.ReadFromJsonAsync(PRFactoryBaseWipJson.Default.PRFactoryReleaseResponse, ct);
        if (ack is not { Released: true } || ack.ReleaseId != request.ReleaseId || ack.VerifiedWipSha != request.VerifiedWipSha)
        {
            throw new InvalidDataException("Server did not acknowledge the exact WIP release.");
        }

        return ack;
    }
}

/// <summary>Handover only after all writers are quiescent and committed work has a verified WIP receipt.</summary>
public sealed class PRFactoryHandover(PRFactoryClient client, PRFactoryHandoverStore store, PublicationAuthority authority)
{
    public async Task<PRFactoryReleaseResponse> ReleaseAsync(PRFactoryWorkItem item, WorkspaceSnapshot workspace,
        Guid machineId, string atfJobId, string reason, Func<bool> writersQuiescent, CancellationToken ct)
    {
        if (!writersQuiescent())
        {
            throw new InvalidOperationException("Release refused: lead or children are still active.");
        }
        if (!await client.SupportsBaseWipAsync(ct))
        {
            throw new InvalidOperationException("Server does not advertise base-wip-v1.");
        }

        if (item.LeaseToken is not Guid lease || item.RepositoryId is not Guid repository || workspace.BaseSha is null)
        {
            throw new InvalidOperationException("Accepted repository identity is missing.");
        }

        var dirty = await TeamWorkspace.Git(workspace.LeadPath, "status", "--porcelain", "--untracked-files=all");
        if (dirty.Length != 0)
        {
            throw new InvalidOperationException("Release refused: uncommitted work remains in the lead checkout.");
        }

        foreach (var member in workspace.Members)
        {
            var status = await TeamWorkspace.Git(member.Path, "status", "--porcelain", "--untracked-files=all");
            if (status.Length != 0)
            {
                throw new InvalidOperationException("Release refused: child workspace has uncommitted work.");
            }
        }
        var wip = store.Wip(workspace.Key);
        var head = JobWorktree.Head(workspace.LeadPath);
        if (wip is not { State: "reported", Receipt.Length: > 0 } || wip.HeadSha != head)
        {
            throw new InvalidOperationException("Release refused: final lead SHA has no verified WIP receipt.");
        }
        var remote = await TeamWorkspace.Git(workspace.LeadPath, "ls-remote", "--heads", "origin", "refs/heads/" + wip.Branch);
        if (remote != wip.HeadSha + "\trefs/heads/" + wip.Branch)
        {
            throw new InvalidOperationException("Release refused: remote WIP tip differs from the receipt.");
        }

        var releaseId = $"{item.Id:D}:{lease:D}:{wip.HeadSha}";
        PRFactoryReleaseResponse? acknowledgement = null;
        if (!await authority(item.Id, async () => acknowledgement = await client.ReleaseWipAsync(item.Id,
            new(reason, lease, machineId, atfJobId, repository, workspace.BaseSha, releaseId, wip.Branch, wip.HeadSha), ct), ct))
        {
            throw new InvalidOperationException("Release authority is not confirmed.");
        }
        store.RecordRelease(workspace.Key, new(releaseId, wip.HeadSha, DateTimeOffset.UtcNow));
        return acknowledgement!;
    }

    /// <summary>Retire only this daemon's inactive worktrees after a matching server receipt and retention.</summary>
    public async Task CleanupReleasedAsync(WorkspaceSnapshot workspace, Func<bool> inactive, TimeSpan retention)
    {
        var release = store.Release(workspace.Key);
        if (!inactive() || release is null || DateTimeOffset.UtcNow < release.AcknowledgedAt + retention)
        {
            throw new InvalidOperationException("Workspace is active or still within retention.");
        }
        var wip = store.Wip(workspace.Key);
        if (wip is not { State: "reported", Receipt.Length: > 0 }
            || release.VerifiedWipSha != wip.HeadSha
            || !Directory.Exists(workspace.Root)
            || workspace.RepositoryPath is null || workspace.Remote is null)
        {
            throw new InvalidOperationException("Release and WIP receipts are required before cleanup.");
        }
        var root = Path.GetFullPath(workspace.Root);
        var repository = Path.GetFullPath(workspace.RepositoryPath);
        var owned = new[] { workspace.LeadPath }.Concat(workspace.Members.Select(m => m.Path)).ToArray();
        if (root == repository || owned.Any(path => !Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Refusing cleanup outside the owned workspace root.");
        }
        var remote = await TeamWorkspace.Git(workspace.LeadPath, "ls-remote", "--heads", "origin", "refs/heads/" + wip.Branch);
        if (!remote.StartsWith(wip.HeadSha + "\trefs/heads/" + wip.Branch, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Remote WIP tip changed; workspace retained.");
        }
        foreach (var path in owned)
        {
            if ((await TeamWorkspace.Git(path, "status", "--porcelain", "--untracked-files=all")).Length != 0)
            {
                throw new InvalidOperationException("Workspace became dirty; cleanup refused.");
            }
        }
        foreach (var path in owned)
        {
            await TeamWorkspace.Git(repository, "worktree", "remove", "--", path);
        }
        Directory.Delete(root, recursive: true);
    }
}
