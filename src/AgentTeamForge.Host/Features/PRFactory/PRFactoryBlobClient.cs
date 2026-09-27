using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Host.Features.PRFactory;

public sealed partial class PRFactoryClient
{
    bool? blobCapability;

    public async Task<bool> SupportsBlobsAsync(CancellationToken ct)
    {
        if (blobCapability is { } known) { return known; }
        using var response = await httpClient.GetAsync("api/work-item-blobs/capabilities", ct);
        RejectToken(response.StatusCode);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
        {
            blobCapability = false;
            return false;
        }
        response.EnsureSuccessStatusCode();
        // A non-JSON 200 (e.g. an HTML fallback page) is an older server, not a transient failure.
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            blobCapability = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("capabilities", out var capabilities)
                && capabilities.ValueKind == JsonValueKind.Array
                && capabilities.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "blob-attachments-v1");
        }
        catch (JsonException) { blobCapability = false; }
        return blobCapability.Value;
    }

    public async Task UploadAttachmentAsync(Guid id, PRFactoryAttachment upload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/work-item-blobs/work-items/{id:D}");
        request.Headers.Add("X-PRFactory-Capability", "blob-attachments-v1");
        request.Content = new ByteArrayContent(upload.Payload);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(upload.ContentType);
        using var response = await httpClient.SendAsync(request, ct);
        RejectToken(response.StatusCode);
        // Unlike the worker endpoints, blob 409 also means an idempotency conflict. Fail visibly;
        // do not reinterpret it as a lost lease and silently abandon the pending attachment.
        if ((int)response.StatusCode is >= 400 and < 500 and not (408 or 429))
        {
            var details = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidDataException($"PRFactory rejected attachment {upload.ClientKey} (HTTP {(int)response.StatusCode}): {details[..Math.Min(details.Length, 2000)]}");
        }
        response.EnsureSuccessStatusCode();
    }
}
