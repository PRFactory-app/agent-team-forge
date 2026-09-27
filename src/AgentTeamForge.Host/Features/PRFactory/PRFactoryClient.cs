using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentTeamForge.Host.Features.PRFactory;

// Worker wire contract ported from PRFactory.Worker/Api/PRFactoryClient.cs
// and Models/MachineRegistrationModels.cs.
public sealed record RegisterMachineRequest(string MachineName, string MachineFingerprint, string? OperatingSystem, string? WorkerVersion,
    string[]? Capabilities = null);
public sealed class RegisterMachineResponse
{
    public Guid MachineId { get; set; }
    public int HeartbeatIntervalSeconds { get; set; }
}
public sealed class MachineHeartbeatResponse
{
    public bool Accepted { get; set; }
    public int HeartbeatIntervalSeconds { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RegisterMachineRequest))]
[JsonSerializable(typeof(RegisterMachineResponse))]
[JsonSerializable(typeof(MachineHeartbeatResponse))]
internal sealed partial class PRFactoryWireJson : JsonSerializerContext;

/// <summary>The server no longer leases this work item to us (cancelled, reaped or reclaimed).</summary>
public sealed class PRFactoryLeaseLostException(Guid id) : Exception($"PRFactory work item {id:D} is no longer leased to this worker");

public sealed class WorkerTokenRejectedException : Exception
{
    public WorkerTokenRejectedException() : base("PRFactory rejected the worker token") { }
}

public sealed class PRFactoryStreamRejectedException(HttpStatusCode statusCode)
    : Exception($"PRFactory agent-stream rejected with HTTP {(int)statusCode}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed partial class PRFactoryClient(HttpClient httpClient)
{
    // Server capability gate: single-repository worker contract, not the ATF product version.
    const string WorkerVersion = "1.0.0";
    // Only semantics that are wired and tested end to end: explicit server dispositions stop and fence
    // owned work; completion carries a pushed, ls-remote-verified branch for remote-only PR creation.
    // Not yet: human-wait-v1, readiness-parking-v1 (no auth/model probes),
    // multi-repo-v1.
    public static readonly string[] Capabilities = ["authority-disposition-v1", "remote-publication-v1", "workspace-continuity-v1", "blob-attachments-v1"];
    bool legacyLogged;
    public enum AcceptanceResult { Confirmed, NotFound, Conflict }
    /// <summary>Server disposition: accepted, completed, cancelled, revoked or reconciliation-needed.</summary>
    public sealed record Acceptance(AcceptanceResult Result, string Disposition, string? Reason = null);

    public void LogLegacyOnce(Action<string>? log)
    {
        if (legacyLogged)
        {
            return;
        }
        legacyLogged = true;
        log?.Invoke("PRFactory durable acceptance endpoint unavailable; using legacy lease behavior");
    }
    public static HttpClient CreateHttpClient(string url, string token, HttpMessageHandler? handler = null)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        client.BaseAddress = new Uri(url.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromMinutes(2); // PRFactory's cold-start allowance.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<RegisterMachineResponse> RegisterMachineAsync(CancellationToken ct)
    {
        var request = new RegisterMachineRequest(Environment.MachineName,
            $"{Environment.MachineName}:{Environment.UserName}",
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            WorkerVersion, Capabilities);
        using var response = await httpClient.PostAsJsonAsync("api/worker/machines/register", request,
            PRFactoryWireJson.Default.RegisterMachineRequest, ct);
        RejectToken(response.StatusCode);
        response.EnsureSuccessStatusCode();
        var registration = await response.Content.ReadFromJsonAsync(PRFactoryWireJson.Default.RegisterMachineResponse, ct);
        if (registration is null || registration.MachineId == Guid.Empty)
        {
            throw new HttpRequestException("PRFactory returned an invalid machine registration");
        }
        return registration;
    }

    public async Task<bool> HeartbeatMachineAsync(Guid machineId, CancellationToken ct)
    {
        using var response = await httpClient.PostAsync($"api/worker/machines/{machineId:D}/heartbeat", null, ct);
        RejectToken(response.StatusCode);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        response.EnsureSuccessStatusCode();
        // The worker contract's heartbeat endpoint may return 204 or a response DTO.
        return true;
    }

    public async Task<IReadOnlyList<PRFactoryWorkItem>> PollAsync(IEnumerable<Guid> repositories, Guid? machineId, CancellationToken ct,
        int maxItems = 10)
    {
        var query = $"maxItems={maxItems}" + string.Concat(repositories.Select(id => $"&repositoryIds={id:D}"))
            + $"&workerVersion={WorkerVersion}" + (machineId is Guid mid ? $"&machineId={mid:D}" : "");
        using var response = await httpClient.GetAsync("api/worker/poll?" + query, ct);
        RejectToken(response.StatusCode);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(PRFactoryWorkItemJson.Default.PRFactoryPollResponse, ct))?.WorkItems ?? [];
    }

    public async Task<PRFactoryWorkItem?> ClaimAsync(Guid id, Guid? machineId, CancellationToken ct)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/worker/claim/{id:D}",
            new PRFactoryClaimRequest(Environment.MachineName, WorkerVersion, machineId), PRFactoryWorkItemJson.Default.PRFactoryClaimRequest, ct);
        RejectToken(response.StatusCode);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(PRFactoryWorkItemJson.Default.PRFactoryClaimResponse, ct))?.WorkItem;
    }

    public async Task<Acceptance> GetAtfAcceptanceAsync(Guid id, Guid machineId, string jobId, CancellationToken ct)
    {
        using var response = await httpClient.GetAsync(
            $"api/worker/work-items/{id:D}/atf-acceptance?machineId={machineId:D}&jobId={Uri.EscapeDataString(jobId)}", ct);
        return await ReadAcceptanceAsync(response, jobId, ct);
    }

    public async Task<Acceptance> AcceptAtfAsync(Guid id, Guid machineId, Guid leaseToken, string jobId, CancellationToken ct)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/worker/work-items/{id:D}/atf-acceptance",
            new PRFactoryAtfAcceptRequest(machineId, leaseToken, jobId), PRFactoryWorkItemJson.Default.PRFactoryAtfAcceptRequest, ct);
        return await ReadAcceptanceAsync(response, jobId, ct);
    }

    public async Task<bool> ConfirmLegacyLeaseAsync(Guid id, Guid leaseToken, CancellationToken ct)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/worker/work-items/{id:D}/heartbeat",
            new PRFactoryLeaseHeartbeatRequest(leaseToken), PRFactoryWorkItemJson.Default.PRFactoryLeaseHeartbeatRequest, ct);
        RejectToken(response.StatusCode);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            return false;
        }
        response.EnsureSuccessStatusCode();
        return true;
    }

    static async Task<Acceptance> ReadAcceptanceAsync(HttpResponseMessage response, string jobId, CancellationToken ct)
    {
        RejectToken(response.StatusCode);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new(AcceptanceResult.NotFound, "reconciliation-needed", "acceptance_not_found");
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return new(AcceptanceResult.Conflict, "reconciliation-needed", "acceptance_conflict");
        }
        response.EnsureSuccessStatusCode();
        var item = await response.Content.ReadFromJsonAsync(PRFactoryWorkItemJson.Default.PRFactoryAtfAcceptanceResponse, ct);
        if (item is null || !string.Equals(item.AtfJobId, jobId, StringComparison.Ordinal))
        {
            throw new HttpRequestException("PRFactory returned a mismatched ATF acceptance");
        }
        var disposition = Disposition(item);
        return new(disposition == "accepted" ? AcceptanceResult.Confirmed : AcceptanceResult.Conflict,
            disposition, item.DispositionReason ?? (disposition == "accepted" ? null : "server_" + disposition));
    }

    // An explicit authority-disposition-v1 value wins; older servers only expose work-item status.
    // Anything unrecognized becomes reconciliation, never acceptance.
    static string Disposition(PRFactoryAtfAcceptanceResponse item)
    {
        if (item.Disposition is { Length: > 0 } explicitDisposition)
        {
            var normalized = explicitDisposition.Replace("_", "-", StringComparison.Ordinal).ToLowerInvariant();
            if (normalized == "reconciliationneeded") { normalized = "reconciliation-needed"; }
            return normalized is "accepted" or "completed" or "cancelled" or "revoked" or "reconciliation-needed"
                ? normalized : "reconciliation-needed";
        }
        var status = item.Status.ValueKind switch
        {
            JsonValueKind.String => item.Status.GetString(),
            JsonValueKind.Number when item.Status.TryGetInt32(out var number) => number switch
            {
                0 => "Pending",
                1 => "Claimed",
                2 => "InProgress",
                3 => "Completed",
                4 => "Failed",
                5 => "Cancelled",
                6 => "ReconciliationNeeded",
                _ => null
            },
            _ => null
        };
        return status?.ToLowerInvariant() switch
        {
            "pending" or "claimed" or "inprogress" => "accepted",
            "completed" => "completed",
            "cancelled" => "cancelled",
            "failed" => "revoked",
            _ => "reconciliation-needed"
        };
    }

    public async Task<IReadOnlyList<PRFactoryCommand>> DrainCommandsAsync(Guid id, Guid lease, CancellationToken ct)
    {
        using var response = await httpClient.GetAsync($"api/worker/work-items/{id:D}/agent-commands?leaseToken={lease:D}", ct);
        RejectToken(response.StatusCode);
        RejectLostLease(response.StatusCode, id);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(PRFactoryWorkItemJson.Default.PRFactoryCommandDrainResponse, ct))?.Commands ?? [];
    }

    public async Task AckCommandsAsync(Guid id, Guid lease, List<PRFactoryCommandAck> acks, CancellationToken ct)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/worker/work-items/{id:D}/agent-commands/ack",
            new PRFactoryCommandAckRequest(lease, acks), PRFactoryWorkItemJson.Default.PRFactoryCommandAckRequest, ct);
        RejectToken(response.StatusCode);
        RejectLostLease(response.StatusCode, id);
        response.EnsureSuccessStatusCode();
    }

    public async Task<PRFactoryStreamResponse> UploadStreamAsync(Guid id, PRFactoryStreamBatch batch, CancellationToken ct)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/worker/work-items/{id:D}/agent-stream",
            batch, PRFactoryWorkItemJson.Default.PRFactoryStreamBatch, ct);
        RejectToken(response.StatusCode);
        RejectLostLease(response.StatusCode, id);
        if ((int)response.StatusCode is >= 400 and < 500 and not (408 or 429))
        {
            throw new PRFactoryStreamRejectedException(response.StatusCode);
        }
        response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync(PRFactoryWorkItemJson.Default.PRFactoryStreamResponse, ct);
        if (receipt?.Accepted != true)
        {
            throw new HttpRequestException("PRFactory did not accept agent stream");
        }

        return receipt;
    }

    public async Task UploadArtefactsAsync(Guid id, Guid? lease, List<PRFactoryArtefactFile> artefacts, CancellationToken ct)
    {
        await UploadArtefactPayloadAsync(id, System.Text.Json.JsonSerializer.Serialize(
            new PRFactoryArtefactRequest(artefacts, lease), PRFactoryWorkItemJson.Default.PRFactoryArtefactRequest), ct);
    }

    public async Task UploadArtefactPayloadAsync(Guid id, string payload, CancellationToken ct)
    {
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync($"api/worker/artefacts/{id:D}", content, ct);
        RejectToken(response.StatusCode);
        RejectLostLease(response.StatusCode, id);
        if ((int)response.StatusCode is >= 400 and < 500 and not (408 or 429))
        {
            var details = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidDataException($"PRFactory rejected artefacts (HTTP {(int)response.StatusCode}): {details[..Math.Min(details.Length, 2000)]}");
        }
        response.EnsureSuccessStatusCode();
    }

    public async Task CompleteAsync(Guid id, Guid? lease, string? markdown, CancellationToken ct,
        string? branch = null, string? commit = null, PRFactoryRemotePublication? publication = null)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/worker/complete/{id:D}",
            new PRFactoryCompletionRequest(true, markdown, branch, commit, string.Empty, lease, publication), PRFactoryWorkItemJson.Default.PRFactoryCompletionRequest, ct);
        RejectToken(response.StatusCode);
        RejectLostLease(response.StatusCode, id);
        response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync(PRFactoryWorkItemJson.Default.PRFactoryCompletionResponse, ct);
        if (receipt?.Accepted != true)
        {
            throw new HttpRequestException("PRFactory did not accept completion");
        }
    }

    public async Task FailAsync(Guid id, Guid? lease, string error, CancellationToken ct, bool shouldRetry = false)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/worker/fail/{id:D}",
            new PRFactoryFailureRequest(error, string.Empty, shouldRetry, string.Empty, lease), PRFactoryWorkItemJson.Default.PRFactoryFailureRequest, ct);
        RejectToken(response.StatusCode);
        RejectLostLease(response.StatusCode, id);
        response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync(PRFactoryWorkItemJson.Default.PRFactoryFailureResponse, ct);
        if (receipt?.Acknowledged != true)
        {
            throw new HttpRequestException("PRFactory did not acknowledge failure");
        }
    }

    static void RejectToken(HttpStatusCode status)
    {
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new WorkerTokenRejectedException();
        }
    }

    static void RejectLostLease(HttpStatusCode status, Guid id)
    {
        if (status is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            throw new PRFactoryLeaseLostException(id);
        }
    }
}
