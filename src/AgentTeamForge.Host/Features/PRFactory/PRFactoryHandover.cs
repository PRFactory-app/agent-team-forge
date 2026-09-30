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
public sealed record PRFactoryHandoverRequest(string RequestId, string Reason, DateTimeOffset RequestedAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PRFactoryWipReport))]
[JsonSerializable(typeof(PRFactoryWipResponse))]
[JsonSerializable(typeof(PRFactoryReleaseRequest))]
[JsonSerializable(typeof(PRFactoryReleaseResponse))]
[JsonSerializable(typeof(PRFactoryBaseConflictRequest))]
[JsonSerializable(typeof(PRFactoryRepositoryFreshnessRequest))]
[JsonSerializable(typeof(PRFactoryCapabilityResponse))]
[JsonSerializable(typeof(PRFactoryHandoverRequest))]
internal sealed partial class PRFactoryBaseWipJson : JsonSerializerContext;

public sealed partial class PRFactoryClient
{
    string[]? serverCapabilities;
    DateTimeOffset capabilityExpires;
    static readonly TimeSpan CapabilityTtl = TimeSpan.FromSeconds(15);

    // A 5xx is transient: registration may advertise the legacy set, but runtime gating retries
    // instead of treating an outage as a server without base-wip-v1/multi-repo-v1.
    async Task<string[]> ServerCapabilitiesAsync(CancellationToken ct, bool legacyOnServerError = false)
    {
        if (serverCapabilities is not null && (clock?.GetUtcNow() ?? DateTimeOffset.UtcNow) < capabilityExpires)
        {
            return serverCapabilities;
        }
        using var response = await httpClient.GetAsync("api/worker/capabilities", ct);
        RejectToken(response.StatusCode);
        if ((int)response.StatusCode >= 500)
        {
            if (!legacyOnServerError) { response.EnsureSuccessStatusCode(); }
            workerVersion = "1.0.0";
            return [];
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            serverCapabilities = [];
        }
        else
        {
            response.EnsureSuccessStatusCode();
            serverCapabilities = (await response.Content.ReadFromJsonAsync(
                PRFactoryBaseWipJson.Default.PRFactoryCapabilityResponse, ct))?.Capabilities ?? [];
        }
        capabilityExpires = (clock?.GetUtcNow() ?? DateTimeOffset.UtcNow) + CapabilityTtl;
        workerVersion = serverCapabilities.Contains("multi-repo-v1", StringComparer.Ordinal) ? "1.1.0" : "1.0.0";
        return serverCapabilities;
    }

    public async Task<bool> SupportsMultiRepoAsync(CancellationToken ct)
    {
        return (await ServerCapabilitiesAsync(ct)).Contains("multi-repo-v1", StringComparer.Ordinal);
    }

    public async Task<bool> SupportsBaseWipAsync(CancellationToken ct)
    {
        return (await ServerCapabilitiesAsync(ct)).Contains("base-wip-v1", StringComparer.Ordinal);
    }

    public async Task<PRFactoryHandoverRequest?> GetHandoverRequestAsync(Guid id, Guid machineId,
        string atfJobId, CancellationToken ct)
    {
        if (!await SupportsBaseWipAsync(ct)) { return null; }
        using var response = await httpClient.GetAsync(
            $"api/worker/work-items/{id:D}/handover-request?machineId={machineId:D}&atfJobId={Uri.EscapeDataString(atfJobId)}", ct);
        RejectToken(response.StatusCode);
        if (response.StatusCode == HttpStatusCode.NoContent) { return null; }
        if (response.StatusCode == HttpStatusCode.Conflict) { throw new PRFactoryLeaseLostException(id); }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(PRFactoryBaseWipJson.Default.PRFactoryHandoverRequest, ct);
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
        await CheckWipStatusAsync(id, response, ct);
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
        await CheckWipStatusAsync(id, response, ct);
    }

    // 422 remote_unverifiable and other 4xx (except token/lease) mean this head can never be acknowledged.
    static async Task CheckWipStatusAsync(Guid id, HttpResponseMessage response, CancellationToken ct)
    {
        RejectToken(response.StatusCode);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new PRFactoryLeaseLostException(id);
        }

        var code = (int)response.StatusCode;
        if (code is >= 400 and < 500)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var error = (string?)body;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    error = e.GetString();
                }
            }
            catch (System.Text.Json.JsonException) { }
            throw new PRFactoryWipRejectedException(code, error);
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
            throw new InvalidOperationException("Server refused WIP release; workspace retained for reconciliation.");
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
        if (Directory.Exists(workspace.StagingPath)
            && Directory.EnumerateFileSystemEntries(workspace.StagingPath).Any())
        {
            throw new InvalidOperationException("Release refused: unreceipted staging files remain.");
        }
        var wip = store.Wip(workspace.Key);
        var head = JobWorktree.Head(workspace.LeadPath);
        if (wip is not { State: "reported", Receipt.Length: > 0 } || wip.HeadSha != head)
        {
            throw new InvalidOperationException("Release refused: final lead SHA has no verified WIP receipt.");
        }
        await RequireIntegratedAsync(workspace, wip.HeadSha, "Release refused: child commits are not in the WIP receipt.");
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
            if (await WorktreeCleanup.NonDisposableIgnoredAsync(path, CancellationToken.None) is not { Count: 0 })
            {
                throw new InvalidOperationException("Workspace has ignored files; cleanup refused.");
            }
        }
        if (Directory.Exists(workspace.StagingPath)
            && Directory.EnumerateFileSystemEntries(workspace.StagingPath).Any())
        {
            throw new InvalidOperationException("Unreceipted staging files remain; cleanup refused.");
        }
        if (JobWorktree.Head(workspace.LeadPath) != wip.HeadSha)
        {
            throw new InvalidOperationException("Lead has commits beyond the WIP receipt; cleanup refused.");
        }
        await RequireIntegratedAsync(workspace, wip.HeadSha, "Child commits are not in the WIP receipt; cleanup refused.");
        foreach (var path in owned)
        {
            await TeamWorkspace.Git(repository, "worktree", "remove", "--", path);
        }
        try
        {
            if (Directory.Exists(workspace.StagingPath)) { Directory.Delete(workspace.StagingPath); }
            Directory.Delete(root); // Unknown artifacts keep the root for inspection.
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Worktrees removed; unknown files keep the workspace root: {ex.Message}");
        }
    }

    // Child checkouts keep their branches after worktree removal, but only the lead receipt is handed over.
    static async Task RequireIntegratedAsync(WorkspaceSnapshot workspace, string receiptSha, string refusal)
    {
        foreach (var member in workspace.Members)
        {
            var child = await TeamWorkspace.Git(member.Path, "rev-parse", "HEAD");
            try
            {
                await TeamWorkspace.Git(workspace.LeadPath, "merge-base", "--is-ancestor", child, receiptSha);
            }
            catch (InvalidOperationException)
            {
                throw new InvalidOperationException(refusal);
            }
        }
    }
}
