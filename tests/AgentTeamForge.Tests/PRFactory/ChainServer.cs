using System.Net;
using System.Text;
using System.Text.Json;
using AgentTeamForge.Host.Features.PRFactory;

namespace AgentTeamForge.Tests.PRFactory;

/// <summary>Minimal PRFactory worker API: one claimable item, durable acceptance, artefacts and completion.</summary>
sealed class ChainServer(PRFactoryWorkItem item)
{
    public const string Url = "https://example.test";
    public PRFactoryWorkItem Item { get; } = item;
    public int Status { get; set; } = 1;
    public string? AcceptedJobId { get; private set; }
    public List<string> Artefacts { get; } = [];
    public List<JsonElement> Completions { get; } = [];
    public List<string> Failures { get; } = [];
    public bool Offered { get; set; } = true;
    public List<PRFactoryCommand> Commands { get; } = [];
    public List<JsonElement> Acks { get; } = [];
    public List<PRFactoryStreamLine> Lines { get; } = [];
    public bool BlobsSupported { get; set; }
    public bool LoseBlobResponse { get; set; }
    public HttpStatusCode? BlobRejection { get; set; }
    public HttpStatusCode? StreamRejection { get; set; }
    public int StreamPosts { get; private set; }
    public List<BlobRequest> Blobs { get; } = [];
    public List<string> PollQueries { get; } = [];
    public List<string> UploadOrder { get; } = [];
    public bool MultiRepoSupported { get; set; }
    public bool BaseWipSupported { get; set; }
    public string? AcceptanceReleaseIdOverride { get; set; }
    public bool HandoverRequested { get; set; }
    /// <summary>Returned with an accepted completion, like the server once the ticket reached Completed.</summary>
    public string? CleanupWipBranch { get; set; }
    /// <summary>Like the real server: a release makes the item pending again with the handover fields for the next claim.</summary>
    public bool ReleaseRequeues { get; set; }
    /// <summary>Pin: only this machine is offered the item once claimed. A release clears it.</summary>
    public Guid? Owner { get; private set; }

    /// <summary>The operator released the server acceptance; the item is offered afresh.</summary>
    public void Requeue() => (AcceptedJobId, AcceptedMachineId, Owner, Status, Offered) = (null, null, null, 1, true);
    public Guid? AcceptedMachineId { get; private set; }
    public List<Guid?> PollMachines { get; } = [];
    public List<Guid?> ClaimMachines { get; } = [];
    public List<JsonElement> WipReports { get; } = [];
    public HttpStatusCode? WipRejection { get; set; }
    public HttpStatusCode? WipFailureReportStatus { get; set; }
    public List<JsonElement> Releases { get; } = [];
    public List<JsonElement> RepositoryResults { get; } = [];
    public Action? OnBlobUpload { get; set; }

    public sealed record BlobRequest(string ContentType, byte[] Payload, Dictionary<string, string> Fields, byte[] File);

    static BlobRequest ReadBlob(HttpRequestMessage request)
    {
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("fake-token", request.Headers.Authorization.Parameter);
        Assert.Equal("blob-attachments-v1", Assert.Single(request.Headers.GetValues("X-PRFactory-Capability")));
        var type = request.Content!.Headers.ContentType!;
        Assert.Equal("multipart/form-data", type.MediaType);
        var boundary = type.Parameters.Single(p => p.Name == "boundary").Value!.Trim('"');
        var payload = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        byte[]? file = null;
        foreach (var part in Encoding.Latin1.GetString(payload).Split("--" + boundary)[1..^1])
        {
            var split = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Assert.True(split >= 0);
            var headers = part[..split].Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var disposition = System.Net.Http.Headers.ContentDispositionHeaderValue.Parse(
                headers.Single(h => h.StartsWith("Content-Disposition:", StringComparison.Ordinal))["Content-Disposition:".Length..].Trim());
            var name = disposition.Name!.Trim('"');
            var bytes = Encoding.Latin1.GetBytes(part[(split + 4)..^2]);
            if (name == "File")
            {
                file = bytes;
                Assert.Equal(fields["FileName"], disposition.FileName!.Trim('"'));
                Assert.Contains("Content-Type: " + fields["MediaType"], headers);
            }
            else { fields.Add(name, Encoding.UTF8.GetString(bytes)); }
        }
        Assert.NotNull(file);
        Assert.Equal(file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), fields["ByteCount"]);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file)), fields["Sha256"]);
        return new(type.ToString(), payload, fields, file);
    }

    public PRFactoryClient Client() =>
        new(PRFactoryClient.CreateHttpClient(Url, "fake-token", new Handler(Reply)));

    HttpResponseMessage Reply(HttpRequestMessage request)
    {
        lock (this)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/worker/capabilities")
            {
                return Json(MultiRepoSupported ? "{\"capabilities\":[\"multi-repo-v1\"]}"
                    : BaseWipSupported ? "{\"capabilities\":[\"base-wip-v1\"]}" : "{\"capabilities\":[]}");
            }
            if (path == "/api/work-item-blobs/capabilities")
            {
                return BlobsSupported ? Json("{\"protocolRevision\":2,\"capabilities\":[\"blob-attachments-v1\"]}")
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            if (path == $"/api/work-item-blobs/work-items/{Item.Id:D}")
            {
                Assert.True(BlobsSupported);
                var blob = ReadBlob(request);
                Assert.Equal(ChainHarness.Machine.ToString("D"), blob.Fields["MachineId"]);
                Assert.Equal(AcceptedJobId, blob.Fields["JobId"]);
                Assert.Equal(Item.LeaseToken!.Value.ToString("D"), blob.Fields["LeaseToken"]);
                Assert.Equal(Item.AttemptCount.ToString(System.Globalization.CultureInfo.InvariantCulture), blob.Fields["Attempt"]);
                Assert.Equal((Item.RepositoryId ?? Guid.Empty).ToString("D"), blob.Fields["RepositoryId"]);
                var previous = Blobs.FirstOrDefault(b => b.Fields["ClientKey"] == blob.Fields["ClientKey"]);
                if (previous is not null) { Assert.Equal(previous.Payload, blob.Payload); Assert.Equal(previous.ContentType, blob.ContentType); }
                Blobs.Add(blob);
                UploadOrder.Add("blob");
                OnBlobUpload?.Invoke();
                if (BlobRejection is { } rejection) { return new HttpResponseMessage(rejection) { Content = new StringContent("blob rejected") }; }
                if (LoseBlobResponse) { LoseBlobResponse = false; throw new HttpRequestException("blob response lost"); }
                return Json("{\"id\":\"" + Guid.NewGuid() + "\"}");
            }
            if (path.EndsWith("/agent-commands", StringComparison.Ordinal))
            {
                return Json(JsonSerializer.Serialize(new PRFactoryCommandDrainResponse([.. Commands]), PRFactoryWorkItemJson.Default.PRFactoryCommandDrainResponse));
            }
            if (path.EndsWith("/agent-stream", StringComparison.Ordinal))
            {
                StreamPosts++;
                if (StreamRejection is { } rejection) { return new HttpResponseMessage(rejection); }
                var stream = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Lines.AddRange(JsonSerializer.Deserialize(stream, PRFactoryWorkItemJson.Default.PRFactoryStreamBatch)!.Lines);
                request.Content = new StringContent(stream);
                return ManagedWire.Reply(request)!;
            }
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            if (path.EndsWith("/handover-request", StringComparison.Ordinal))
            {
                Assert.True(BaseWipSupported);
                Assert.Contains("machineId=" + (AcceptedMachineId ?? ChainHarness.Machine).ToString("D"), request.RequestUri.Query);
                Assert.Contains("atfJobId=" + Uri.EscapeDataString(AcceptedJobId!), request.RequestUri.Query);
                return HandoverRequested ? Json("{\"requestId\":\"request-1\",\"reason\":\"move\",\"requestedAt\":\"2026-09-28T00:00:00Z\"}")
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (path.EndsWith("/wip-publication", StringComparison.Ordinal))
            {
                var report = JsonElement.Parse(body!);
                WipReports.Add(report);
                if (WipFailureReportStatus is { } failing && !report.GetProperty("succeeded").GetBoolean())
                {
                    return new HttpResponseMessage(failing);
                }
                if (WipRejection is { } rejected)
                {
                    return new HttpResponseMessage(rejected) { Content = new StringContent("{\"error\":\"Invalid GitHub URL format\"}") };
                }
                return Json("{\"accepted\":true,\"receiptId\":\"" + report.GetProperty("publicationId").GetString()
                    + "\",\"verifiedHeadSha\":\"" + report.GetProperty("headSha").GetString() + "\"}");
            }
            if (path.Contains("/release/", StringComparison.Ordinal))
            {
                var release = JsonElement.Parse(body!);
                Releases.Add(release);
                UploadOrder.Add("release");
                if (ReleaseRequeues)
                {
                    Assert.Equal(Owner, release.GetProperty("machineId").GetGuid());
                    Assert.Equal(Item.LeaseToken, release.GetProperty("leaseToken").GetGuid());
                    (Owner, AcceptedMachineId, Item.LeaseToken) = (null, null, null);
                }
                HandoverRequested = false;
                if (ReleaseRequeues)
                {
                    Item.HandoverReleaseId = release.GetProperty("releaseId").GetString();
                    Item.HandoverRepositoryId = release.GetProperty("repositoryId").GetGuid();
                    Item.HandoverBaseCommitSha = release.GetProperty("baseCommitSha").GetString();
                    Item.StartFromBranch = release.GetProperty("wipBranchName").GetString();
                    Item.StartCommitSha = release.GetProperty("verifiedWipSha").GetString();
                    (AcceptedJobId, Status, Offered) = (null, 1, true);
                }
                return Json("{\"released\":true,\"releaseId\":\"" + release.GetProperty("releaseId").GetString()
                    + "\",\"verifiedWipSha\":\"" + release.GetProperty("verifiedWipSha").GetString() + "\"}");
            }
            if (path.EndsWith("/repository-result", StringComparison.Ordinal))
            {
                RepositoryResults.Add(JsonElement.Parse(body!));
                return Json("{\"accepted\":true}");
            }
            if (path.EndsWith("/agent-commands/ack", StringComparison.Ordinal))
            {
                foreach (var ack in JsonElement.Parse(body!).GetProperty("acks").EnumerateArray())
                {
                    Acks.Add(ack);
                    Commands.RemoveAll(c => c.CommandId == ack.GetProperty("commandId").GetGuid());
                }
                return Json("{\"applied\":1}");
            }
            if (path.EndsWith("/poll", StringComparison.Ordinal))
            {
                PollQueries.Add(request.RequestUri.Query);
                var machine = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["machineId"] is { } polled ? Guid.Parse(polled) : (Guid?)null;
                PollMachines.Add(machine);
                if (Owner is not null && Owner != machine) { return Json("{\"workItems\":[]}"); }
                return Json(Offered ? "{\"workItems\":[" + ItemJson() + "]}" : "{\"workItems\":[]}");
            }
            if (path.Contains("/claim/", StringComparison.Ordinal))
            {
                Offered = false;
                var claimed = JsonElement.Parse(body!).GetProperty("machineId").GetGuid();
                ClaimMachines.Add(claimed);
                Owner = claimed;
                if (ReleaseRequeues) { Item.LeaseToken = Guid.NewGuid(); }
                return Json("{\"workItem\":" + ItemJson() + "}");
            }
            if (path.EndsWith("/atf-acceptance", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Post)
                {
                    var accept = JsonElement.Parse(body!);
                    if (ReleaseRequeues && (accept.GetProperty("leaseToken").GetGuid() != Item.LeaseToken
                        || accept.GetProperty("machineId").GetGuid() != Owner))
                    {
                        return new HttpResponseMessage(HttpStatusCode.Conflict);
                    }
                    if (AcceptedJobId is null)
                    {
                        AcceptedJobId = accept.GetProperty("jobId").GetString();
                        AcceptedMachineId = accept.GetProperty("machineId").GetGuid();
                    }
                }
                else if (ReleaseRequeues && System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["machineId"]
                    != AcceptedMachineId?.ToString("D"))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound); // Revoked for every other machine.
                }
                return AcceptedJobId is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : Json(JsonSerializer.Serialize(new PRFactoryAtfAcceptanceResponse(AcceptedJobId,
                        JsonElement.Parse(Status.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        HandoverReleaseId: AcceptanceReleaseIdOverride ?? Item.HandoverReleaseId,
                        HandoverRepositoryId: Item.HandoverRepositoryId,
                        HandoverBaseCommitSha: Item.HandoverBaseCommitSha,
                        StartFromBranch: Item.StartFromBranch, StartCommitSha: Item.StartCommitSha),
                        PRFactoryWorkItemJson.Default.PRFactoryAtfAcceptanceResponse));
            }
            if (Status is 5 or 6 && (path.Contains("/artefacts/", StringComparison.Ordinal)
                || path.Contains("/complete/", StringComparison.Ordinal) || path.Contains("/fail/", StringComparison.Ordinal)))
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict); // Stale authority is rejected server-side too.
            }
            if (path.Contains("/artefacts/", StringComparison.Ordinal))
            {
                UploadOrder.Add("artefacts");
                Artefacts.Add(body!);
                return Json("{\"accepted\":true}");
            }
            if (path.Contains("/complete/", StringComparison.Ordinal))
            {
                UploadOrder.Add("complete");
                Completions.Add(JsonElement.Parse(body!));
                Status = 3;
                return Json(JsonSerializer.Serialize(new PRFactoryCompletionResponse(true, CleanupWipBranch),
                    PRFactoryWorkItemJson.Default.PRFactoryCompletionResponse));
            }
            if (path.Contains("/fail/", StringComparison.Ordinal))
            {
                Failures.Add(body!);
                Status = 4;
                return Json("{\"acknowledged\":true}");
            }
            throw new InvalidOperationException(path);
        }
    }

    string ItemJson() => JsonSerializer.Serialize(Item, PRFactoryWorkItemJson.Default.PRFactoryWorkItem);

    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(reply(request));
    }
}
