using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AgentTeamForge.Host.Hosting;

namespace AgentTeamForge.Host.Features.PRFactory;

/// <summary>The account the device link was approved for; not secret, kept for `atf prfactory status`.</summary>
public sealed record PRFactoryLink(string TenantName, string ApprovedBy, string? WorkerTokenId, string? MachineName);

/// <summary>
/// `atf login`: links this machine to a PRFactory workspace with the device flow of PRFactory's device-link contract
/// (docs/architecture/device-link-contract.md). The worker token arrives over HTTPS and is written only to the owner-private
/// token file; it is never printed, logged or taken from the command line.
/// </summary>
public static class PRFactoryLogin
{
    public const string DefaultUrl = "https://app.prfactory.dev";
    public const string ClientId = "atf";
    static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(60);
    static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(5);
    const int MaxCodeAttempts = 5;

    public static async Task<int> RunAsync(StateDirectory state, IReadOnlyDictionary<string, string> options, TextWriter output, TextWriter error,
        HttpMessageHandler? handler = null, Action<string>? openBrowser = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? clock = null, CancellationToken ct = default)
    {
        clock ??= TimeProvider.System;
        delay ??= Task.Delay;
        if (PRFactoryConnection.ServerUrl(options.GetValueOrDefault("url") ?? DefaultUrl) is not { } url)
        {
            error.WriteLine("error: require an HTTPS --url (the server root, for example https://prfactory.example.com)");
            return 64;
        }
        string? caFile = null;
        if (options.TryGetValue("ca-file", out var caPath))
        {
            caFile = PRFactoryConnection.CaFile(caPath);
            if (caFile is null)
            {
                error.WriteLine("error: --ca-file must be a readable PEM file with at least one certificate");
                return 64;
            }
        }
        if (PRFactoryConnection.LinkConflict(state, url) is { } connected)
        {
            error.WriteLine($"error: this machine is connected to {connected}; run atf prfactory disconnect first to link it to {url}");
            return 1;
        }

        using var http = new HttpClient(handler ?? (caFile is null ? new SocketsHttpHandler() : PRFactoryTrust.CreateHandler(caFile)), disposeHandler: true)
        {
            BaseAddress = new Uri(url + "/"),
            Timeout = TimeSpan.FromMinutes(2), // PRFactory's cold-start allowance.
        };
        try
        {
            var code = await RequestCodeAsync(http, delay, error, ct);
            if (code is null) { return 1; }
            var expires = clock.GetUtcNow() + code.Value.ExpiresIn; // Before any output or browser work.
            output.WriteLine($"To link this machine, open {code.Value.CompleteUri}");
            output.WriteLine($"Your code is {code.Value.UserCode}. Compare it with the code on the page before you select Link.");
            output.WriteLine($"If the browser does not open, go to {code.Value.Uri} and type the code.");
            // Only a code this command requested is ever opened (contract §7), and only over HTTPS.
            if (!options.ContainsKey("no-browser") && Uri.TryCreate(code.Value.CompleteUri, UriKind.Absolute, out var page) && page.Scheme == Uri.UriSchemeHttps)
            {
                (openBrowser ?? OpenBrowser)(page.AbsoluteUri);
            }
            output.WriteLine("Waiting for the link in the browser...");
            return await PollAsync(state, http, url, caFile, code.Value, expires, delay, clock, output, error, ct);
        }
        catch (HttpRequestException ex)
        {
            error.WriteLine($"error: cannot reach {url}: {ex.Message}");
            return 1;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            error.WriteLine($"error: {url} did not answer in time");
            return 1;
        }
    }

    readonly record struct DeviceCode(string Device, string UserCode, string Uri, string CompleteUri, TimeSpan ExpiresIn, TimeSpan Interval);

    static async Task<DeviceCode?> RequestCodeAsync(HttpClient http, Func<TimeSpan, CancellationToken, Task> delay, TextWriter error, CancellationToken ct)
    {
        var machine = new string([.. Environment.MachineName.Trim().Where(c => !char.IsControl(c))]);
        machine = machine.Length == 0 ? "atf-machine" : machine[..Math.Min(machine.Length, 100)];
        for (var attempt = 1; ; attempt++)
        {
            using var response = await http.PostAsync("api/device-link/code",
                new FormUrlEncodedContent([new("client_id", ClientId), new("machine_name", machine)]), ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxCodeAttempts)
            {
                await delay(RetryAfter(response), ct);
                continue;
            }
            using var json = await ReadJsonAsync(response, ct);
            if (response.StatusCode != HttpStatusCode.OK || json is null
                || Text(json, "device_code") is not { } device || Text(json, "user_code") is not { } user
                || Text(json, "verification_uri") is not { } uri || Text(json, "verification_uri_complete") is not { } complete)
            {
                error.WriteLine($"error: PRFactory refused the device link request ({(int)response.StatusCode}){Describe(json)}");
                return null;
            }
            return new(device, user, uri, complete, TimeSpan.FromSeconds(Number(json, "expires_in") ?? 600),
                TimeSpan.FromSeconds(Math.Max(1, Number(json, "interval") ?? 5)));
        }
    }

    static async Task<int> PollAsync(StateDirectory state, HttpClient http, string url, string? caFile, DeviceCode code, DateTimeOffset deadline,
        Func<TimeSpan, CancellationToken, Task> delay, TimeProvider clock, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var interval = code.Interval;
        var wait = interval;
        var transientReported = false;
        while (true)
        {
            // No wait outlasts the code: a long Retry-After or interval ends at the expiry.
            var left = deadline - clock.GetUtcNow();
            await delay(wait < left ? wait : left > TimeSpan.Zero ? left : TimeSpan.Zero, ct);
            wait = interval;
            if (clock.GetUtcNow() >= deadline)
            {
                error.WriteLine("error: the device code has expired. Run atf login again.");
                return 1;
            }
            HttpResponseMessage response;
            try
            {
                response = await http.PostAsync("api/device-link/token", new FormUrlEncodedContent([
                    new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"), new("device_code", code.Device), new("client_id", ClientId)]), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                // A network blip during the wait is not the user's answer; keep polling until the code expires.
                if (!transientReported) { error.WriteLine($"warning: {url} did not answer; still waiting"); transientReported = true; }
                continue;
            }
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    wait = RetryAfter(response);
                    continue;
                }
                if ((int)response.StatusCode >= 500)
                {
                    if (!transientReported) { error.WriteLine($"warning: {url} answered {(int)response.StatusCode}; still waiting"); transientReported = true; }
                    continue;
                }
                using var json = await ReadJsonAsync(response, ct);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return Save(state, url, caFile, json, output, error);
                }
                switch (json is null ? null : Text(json, "error"))
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        // Each slow_down raises the interval for the rest of the flow, at most to 60 s.
                        var raised = Number(json!, "interval") is { } seconds ? TimeSpan.FromSeconds(seconds) : interval + SlowDownStep;
                        interval = wait = raised > MaxInterval ? MaxInterval : raised < interval ? interval : raised;
                        continue;
                    case "access_denied" or "expired_token" or "invalid_grant":
                        error.WriteLine($"error: {Text(json!, "error_description") ?? Text(json!, "error")} Run atf login again.");
                        return 1;
                    default:
                        error.WriteLine($"error: PRFactory refused the device link ({(int)response.StatusCode}){Describe(json)}");
                        return 1;
                }
            }
        }
    }

    static int Save(StateDirectory state, string url, string? caFile, JsonDocument? json, TextWriter output, TextWriter error)
    {
        if (json is null || Text(json, "access_token") is not { Length: > 0 } token || token.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            error.WriteLine("error: PRFactory returned no usable worker token. Run atf login again.");
            return 1;
        }
        var link = new PRFactoryLink(Text(json, "tenant_name") ?? "(unnamed workspace)", Text(json, "approved_by") ?? "(unknown)",
            Text(json, "worker_token_id"), Text(json, "machine_name"));
        if (PRFactoryConnection.SaveLinked(state, url, caFile, token, link) is { } conflict)
        {
            error.WriteLine($"error: this machine is connected to {conflict}; the link was not saved. Run atf prfactory disconnect, then atf login again.");
            return 1;
        }
        // Shown so that a link approved by someone else is visible on this machine (contract §7).
        output.WriteLine($"Linked to {link.TenantName} as {link.ApprovedBy}.");
        output.WriteLine("The daemon serves this workspace's work without a repository mapping. Map a local checkout with "
            + "atf prfactory connect --repo <repository-id>=<dir> (it reuses this credential).");
        return 0;
    }

    static TimeSpan RetryAfter(HttpResponseMessage response) => response.Headers.RetryAfter switch
    {
        { Delta: { } delta } => delta > TimeSpan.Zero ? delta : TimeSpan.FromSeconds(1),
        { Date: { } date } when date - DateTimeOffset.UtcNow is var until && until > TimeSpan.Zero => until,
        _ => TimeSpan.FromSeconds(5),
    };

    static async Task<JsonDocument?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.ValueKind == JsonValueKind.Object) { return json; }
            json.Dispose();
            return null;
        }
        catch (JsonException) { return null; }
    }

    static string? Text(JsonDocument json, string name) =>
        json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static int? Number(JsonDocument json, string name) =>
        json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            && number > 0 ? number : null;

    // Only the server's error code and text, never a body that could echo request data.
    static string Describe(JsonDocument? json) => json is not null && Text(json, "error") is { } code
        ? $": {code}" + (Text(json, "error_description") is { } text ? $" ({text})" : "") : "";

    /// <summary>Opens the page in the default browser; a failure only means the user follows the printed link.</summary>
    static void OpenBrowser(string url)
    {
        var info = OperatingSystem.IsWindows() ? new ProcessStartInfo("rundll32") { ArgumentList = { "url.dll,FileProtocolHandler", url } }
            : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open") { ArgumentList = { url } }
            : new ProcessStartInfo("xdg-open") { ArgumentList = { url } };
        info.UseShellExecute = false;
        info.RedirectStandardOutput = info.RedirectStandardError = true;
        try { using var _ = Business.Features.Processes.NonInteractiveProcess.Start(info); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
    }
}
