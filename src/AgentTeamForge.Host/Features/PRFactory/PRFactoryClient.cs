using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace AgentTeamForge.Host.Features.PRFactory;

// Registration and heartbeat wire contract ported from PRFactory.Worker/Api/PRFactoryClient.cs
// and Models/MachineRegistrationModels.cs. Work-item methods belong to later slices.
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

    static void RejectToken(HttpStatusCode status)
    {
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new WorkerTokenRejectedException();
        }
    }
}
