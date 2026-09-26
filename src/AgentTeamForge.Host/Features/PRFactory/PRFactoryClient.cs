using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace AgentTeamForge.Host.Features.PRFactory;

// Worker wire contract ported from PRFactory.Worker/Api/PRFactoryClient.cs
// and Models/MachineRegistrationModels.cs.
public sealed record RegisterMachineRequest(string MachineName, string MachineFingerprint, string? OperatingSystem, string? WorkerVersion);
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

public sealed class WorkerTokenRejectedException : Exception
{
    public WorkerTokenRejectedException() : base("PRFactory rejected the worker token") { }
}

public sealed class PRFactoryClient(HttpClient httpClient)
{
    const string WorkerVersion = "0.1.0";
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
            typeof(PRFactoryClient).Assembly.GetName().Version?.ToString());
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

    public async Task<IReadOnlyList<PRFactoryWorkItem>> PollAsync(IEnumerable<Guid> repositories, Guid? machineId, CancellationToken ct)
    {
        var query = "maxItems=10" + string.Concat(repositories.Select(id => $"&repositoryIds={id:D}"))
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

    public async Task UploadArtefactsAsync(Guid id, Guid? lease, List<PRFactoryArtefactFile> artefacts, CancellationToken ct)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/worker/artefacts/{id:D}",
            new PRFactoryArtefactRequest(artefacts, lease), PRFactoryWorkItemJson.Default.PRFactoryArtefactRequest, ct);
        RejectToken(response.StatusCode);
        response.EnsureSuccessStatusCode();
    }

    public async Task CompleteAsync(Guid id, Guid? lease, string? markdown, CancellationToken ct)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/worker/complete/{id:D}",
            new PRFactoryCompletionRequest(true, markdown, null, null, string.Empty, lease), PRFactoryWorkItemJson.Default.PRFactoryCompletionRequest, ct);
        RejectToken(response.StatusCode);
        response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync(PRFactoryWorkItemJson.Default.PRFactoryCompletionResponse, ct);
        if (receipt?.Accepted != true)
        {
            throw new HttpRequestException("PRFactory did not accept completion");
        }
    }

    public async Task FailAsync(Guid id, Guid? lease, string error, CancellationToken ct)
    {
        using var response = await httpClient.PostAsJsonAsync($"api/worker/fail/{id:D}",
            new PRFactoryFailureRequest(error, string.Empty, false, string.Empty, lease), PRFactoryWorkItemJson.Default.PRFactoryFailureRequest, ct);
        RejectToken(response.StatusCode);
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
}
