using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Host.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace AgentTeamForge.Host.Features.WebConsole;

/// <summary>Browser follow-up body. The key comes from the page and is reused only by an explicit operator retry.</summary>
public sealed record WebFollowUpBody(string? Instruction, string? IdempotencyKey, bool Interrupt = false, string? Model = null, string? Effort = null);
public sealed record WebSubmitBody(string? Backend, string? Instruction, string? IdempotencyKey, string? Cwd,
    string? Model = null, string? Effort = null, string? LeadSessionId = null, string? Workspace = null, string? Name = null, string? HerdrPlacement = null);
public sealed record WebJoinTicketBody(string? Name, string? Workspace, string? Note = null);
public sealed record WebLeadMessageBody(string? Text, string? Workspace, string? IdempotencyKey);
public sealed record WebRetentionBody(int? MaxRetainedSessions, JsonElement IdleCloseMinutes);
public sealed record WebTierBody(string? Backend, string? Tier, string? Model, string? Effort, bool ResetAll = false);
public sealed record WebHerdrPlacementBody(string? HerdrPlacement);
public sealed record WebDirectoryEntry(string Name, string Path);
public sealed record WebDirectoryList(string Path, string? Parent, IReadOnlyList<WebDirectoryEntry> Directories);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(WebFollowUpBody))]
[JsonSerializable(typeof(WebSubmitBody))]
[JsonSerializable(typeof(WebJoinTicketBody))]
[JsonSerializable(typeof(WebLeadMessageBody))]
[JsonSerializable(typeof(WebTierBody))]
[JsonSerializable(typeof(WebRetentionBody))]
[JsonSerializable(typeof(WebHerdrPlacementBody))]
[JsonSerializable(typeof(WebDirectoryList))]
public sealed partial class WebConsoleJson : JsonSerializerContext;

/// <summary>
/// Text-only operator console: a thin HTTP client role over the daemon IPC.
/// Never opens the job database or starts the daemon; every job operation is
/// forwarded to <c>send</c> (the <see cref="IpcClient"/> in production).
/// </summary>
/// <remarks>
/// Security: numeric loopback only; exact Host on every request; a scoped random
/// bearer (not the daemon credential) on every API call; exact Origin on POST;
/// no cookies, CORS or query-string credentials; bounded bodies and concurrency.
/// Lost or ambiguous IPC outcomes are forwarded as <c>outcome_unknown</c>; this
/// layer never retries a mutation or mints an idempotency key.
/// </remarks>
public sealed class WebConsoleServer : IAsyncDisposable
{
    // Room for MaxInstructionChars even when every UTF-16 char is JSON-escaped (6 bytes).
    public const int MaxInstructionChars = SpikeLimits.DefaultMaxInstructionChars;
    public const int MaxBodyBytes = 512 * 1024;
    public const int MaxKeyChars = 128;
    public const int MaxConcurrentCalls = 4;

    public const string BadHost = "web_bad_host";
    public const string Unauthorized = "web_unauthorized";
    public const string ForbiddenOrigin = "web_forbidden_origin";
    public const string BadRequest = "web_bad_request";
    public const string Busy = "web_busy";
    public const string NotFound = "web_not_found";

    static readonly (string Path, string Resource, string ContentType)[] Assets =
    [
        ("/", "index.html", "text/html; charset=utf-8"),
        ("/app.js", "app.js", "text/javascript; charset=utf-8"),
        ("/app.css", "app.css", "text/css; charset=utf-8"),
    ];

    const string Csp = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; "
        + "img-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    readonly WebApplication _app;
    readonly Func<IpcRequest, CancellationToken, Task<IpcResponse>> _send;
    readonly Func<string> _token;
    readonly SemaphoreSlim _calls = new(MaxConcurrentCalls, MaxConcurrentCalls);
    string _host = string.Empty;
    string _origin = string.Empty;

    WebConsoleServer(WebApplication app, Func<IpcRequest, CancellationToken, Task<IpcResponse>> send, Func<string> token)
    {
        _app = app;
        _send = send;
        _token = token;
    }

    public int Port { get; private set; }

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Binds 127.0.0.1:<paramref name="port"/> (0 = ephemeral) and starts serving.</summary>
    public static Task<WebConsoleServer> StartAsync(int port, string token, Func<IpcRequest, CancellationToken, Task<IpcResponse>> send) =>
        StartAsync(port, () => token, send);

    /// <summary>Reads the current bearer on each API call so rotation takes effect without a daemon restart.</summary>
    public static async Task<WebConsoleServer> StartAsync(int port, Func<string> token, Func<IpcRequest, CancellationToken, Task<IpcResponse>> send)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThan(token().Length, 32);

        // Empty builder: no config files, env-var URLs or logging providers (headers are never logged).
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Listen(IPAddress.Loopback, port);
            k.Limits.MaxRequestBodySize = MaxBodyBytes;
            k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            k.Limits.MaxRequestLineSize = 4 * 1024;
            k.Limits.MaxConcurrentConnections = 32;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        });
        var app = builder.Build();
        var server = new WebConsoleServer(app, send, token);
        app.Run(server.HandleAsync);
        try { await app.StartAsync(); }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        server.Port = new Uri(address).Port;
        server._host = $"127.0.0.1:{server.Port}";
        server._origin = $"http://{server._host}";
        return server;
    }

    public string Url => _origin + "/";

    async Task HandleAsync(HttpContext ctx)
    {
        var request = ctx.Request;
        var headers = ctx.Response.Headers;
        headers.ContentSecurityPolicy = Csp;
        headers.CacheControl = "no-store";
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";

        if (!string.Equals(request.Host.Value, _host, StringComparison.Ordinal))
        {
            await Reject(ctx, StatusCodes.Status421MisdirectedRequest, BadHost);
            return;
        }

        var path = request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/api/", StringComparison.Ordinal))
        {
            await ServeAssetAsync(ctx, path);
            return;
        }

        if (!HasToken(request))
        {
            await Reject(ctx, StatusCodes.Status401Unauthorized, Unauthorized);
            return;
        }

        var origin = request.Headers.Origin;
        var isMutation = HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method);
        if ((isMutation || origin.Count > 0) && (origin.Count != 1 || !string.Equals(origin[0], _origin, StringComparison.Ordinal)))
        {
            await Reject(ctx, StatusCodes.Status403Forbidden, ForbiddenOrigin);
            return;
        }

        if (HttpMethods.IsGet(request.Method) && path == "/api/directories")
        {
            await ListDirectoriesAsync(ctx);
            return;
        }

        var segments = path["/api/".Length..].Split('/');
        var ipc = (request.Method, segments) switch
        {
            ("GET", ["jobs"]) => new IpcRequest
            {
                Op = IpcProtocol.JobList,
                Status = request.Query["status"].Count == 0 ? null : request.Query["status"].ToString(),
                Cursor = request.Query["cursor"].Count == 0 ? null : request.Query["cursor"].ToString(),
                OrderByActivity = true,
                IncludeUsage = true,
                IncludeConnector = true,
                Limit = ListJobs.MaxPageSize,
            },
            ("GET", ["config"]) => new IpcRequest { Op = IpcProtocol.JobCapabilities },
            ("GET", ["settings", "retention"]) => new IpcRequest { Op = IpcProtocol.RetentionSettingsGet },
            ("PUT", ["settings", "retention"]) => await ReadRetentionAsync(ctx),
            ("GET", ["settings", "tiers"]) => new IpcRequest { Op = IpcProtocol.TierSettingsGet },
            ("PUT", ["settings", "tiers"]) => await ReadTierAsync(ctx),
            ("GET", ["settings", "herdr-placement"]) => new IpcRequest { Op = IpcProtocol.HerdrPlacementGet },
            ("PUT", ["settings", "herdr-placement"]) => await ReadHerdrPlacementAsync(ctx),
            ("POST", ["jobs"]) => await ReadSubmitAsync(ctx),
            ("GET", ["jobs", var id]) when ValidId(id) => new IpcRequest { Op = IpcProtocol.JobGet, JobId = id, IncludeConnector = true },
            ("GET", ["jobs", var id, "output"]) when ValidId(id) => new IpcRequest
            {
                Op = IpcProtocol.JobOutput,
                IncludeConnector = true,
                JobId = id,
                Offset = long.TryParse(request.Query["offset"], out var offset) && offset >= 0 ? offset : 0,
                MaxBytes = int.TryParse(request.Query["max_bytes"], out var max) && max is > 0 and <= 65536 ? max : 65536,
            },
            ("GET", ["jobs", var id, "activity"]) when ValidId(id) => new IpcRequest
            {
                Op = IpcProtocol.JobActivity,
                IncludeConnector = true,
                JobId = id,
                AfterCursor = long.TryParse(request.Query["after_cursor"], out var after) && after >= 0 ? after : 0,
                Limit = int.TryParse(request.Query["limit"], out var count) && count is > 0 and <= JobActivity.MaxPageSize ? count : 20,
            },
            ("POST", ["jobs", var id, "follow-up"]) when ValidId(id) => await ReadFollowUpAsync(ctx, id),
            ("POST", ["jobs", var id, "stop"]) when ValidId(id) => new IpcRequest { Op = IpcProtocol.JobStop, JobId = id },
            ("POST", ["jobs", var id, "stop-agent"]) when ValidId(id) => new IpcRequest { Op = IpcProtocol.JobStopAgent, JobId = id },
            ("POST", ["leads", var id, "join-ticket"]) when Guid.TryParseExact(id, "D", out _) => await ReadJoinTicketAsync(ctx, id),
            ("POST", ["leads", var id, "messages"]) when Guid.TryParseExact(id, "D", out _) => await ReadLeadMessageAsync(ctx, id),
            _ => null,
        };
        if (ipc is null)
        {
            if (!ctx.Response.HasStarted)
            {
                await Reject(ctx, isMutation ? StatusCodes.Status400BadRequest : StatusCodes.Status404NotFound, isMutation ? BadRequest : NotFound);
            }

            return;
        }

        if (!await _calls.WaitAsync(0))
        {
            await Reject(ctx, StatusCodes.Status503ServiceUnavailable, Busy);
            return;
        }

        IpcResponse response;
        try
        {
            // Not tied to the browser connection: a closed tab must not abandon an in-flight mutation.
            response = await _send(ipc, CancellationToken.None);
        }
        finally
        {
            _calls.Release();
        }

        await WriteAsync(ctx, StatusCodes.Status200OK, response);
    }

    static async Task ListDirectoriesAsync(HttpContext ctx)
    {
        var values = ctx.Request.Query["path"];
        if (values.Count != 1 || values[0] is not { Length: > 0 and <= 4096 } path
            || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
        {
            await Reject(ctx, StatusCodes.Status400BadRequest, BadRequest);
            return;
        }

        try
        {
            var directory = new DirectoryInfo(Path.GetFullPath(path));
            var children = directory.EnumerateDirectories()
                .Where(child => (child.Attributes & FileAttributes.ReparsePoint) == 0)
                .Take(100)
                .Select(child => new WebDirectoryEntry(child.Name, child.FullName))
                .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var parent = directory.Parent?.FullName;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(ctx.Response.Body, new WebDirectoryList(directory.FullName, parent, children), WebConsoleJson.Default.WebDirectoryList);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Reject(ctx, StatusCodes.Status400BadRequest, BadRequest);
        }
    }

    static async Task<IpcRequest?> ReadFollowUpAsync(HttpContext ctx, string jobId)
    {
        var body = await ReadBodyAsync(ctx, WebConsoleJson.Default.WebFollowUpBody);

        if (body is not { Instruction: { Length: > 0 and <= MaxInstructionChars } instruction, IdempotencyKey: { Length: > 0 and <= MaxKeyChars } key }
            || string.IsNullOrWhiteSpace(instruction))
        {
            return null;
        }

        return new IpcRequest
        {
            Op = IpcProtocol.JobFollowUp,
            JobId = jobId,
            Instruction = instruction,
            IdempotencyKey = key,
            Interrupt = body.Interrupt,
            Model = body.Model,
            Effort = body.Effort,
        };
    }

    static async Task<IpcRequest?> ReadRetentionAsync(HttpContext ctx)
    {
        var body = await ReadBodyAsync(ctx, WebConsoleJson.Default.WebRetentionBody);
        if (body?.MaxRetainedSessions is not (>= 0 and <= 64)) { return null; }
        int minutes;
        if (body.IdleCloseMinutes.ValueKind == JsonValueKind.String && body.IdleCloseMinutes.GetString() == "off") { minutes = -1; }
        else if (body.IdleCloseMinutes.ValueKind != JsonValueKind.Number || !body.IdleCloseMinutes.TryGetInt32(out minutes)
            || minutes is < 0 or > 1440) { return null; }
        return new IpcRequest { Op = IpcProtocol.RetentionSettingsPut, MaxRetainedSessions = body.MaxRetainedSessions, IdleCloseMinutes = minutes };
    }

    static async Task<IpcRequest?> ReadTierAsync(HttpContext ctx)
    {
        var body = await ReadBodyAsync(ctx, WebConsoleJson.Default.WebTierBody);
        if (body is null)
        {
            return null;
        }

        return new IpcRequest
        {
            Op = IpcProtocol.TierSettingsPut,
            Backend = body.Backend,
            Tier = body.Tier,
            Model = body.Model,
            Effort = body.Effort,
            ResetAllTiers = body.ResetAll
        };
    }

    static async Task<IpcRequest?> ReadHerdrPlacementAsync(HttpContext ctx)
    {
        var body = await ReadBodyAsync(ctx, WebConsoleJson.Default.WebHerdrPlacementBody);
        return body?.HerdrPlacement is null ? null : new IpcRequest
        { Op = IpcProtocol.HerdrPlacementPut, HerdrPlacement = body.HerdrPlacement };
    }

    static async Task<IpcRequest?> ReadSubmitAsync(HttpContext ctx)
    {
        var body = await ReadBodyAsync(ctx, WebConsoleJson.Default.WebSubmitBody);
        if (body is not
            {
                Backend: "claude" or "codex" or "pi" or "cursor" or "droid", Instruction: { Length: > 0 and <= MaxInstructionChars } instruction,
                IdempotencyKey: { Length: > 0 and <= MaxKeyChars } key, Cwd: { Length: > 0 and <= 4096 } cwd
            }
            || string.IsNullOrWhiteSpace(instruction) || !Path.IsPathFullyQualified(cwd) || !Directory.Exists(cwd)
            || !ModelSelection.ValidConsoleSelection(body.Backend, body.Model, body.Effort)
            || body.Name is not null && (!AcceptJob.ValidAgentName(body.Name) || body.Name == "fake-agent")
            || (body.LeadSessionId is null) != (body.Workspace is null)
            || body.LeadSessionId is not null && (!Guid.TryParseExact(body.LeadSessionId, "D", out _)
                || !Path.IsPathFullyQualified(body.Workspace!)))
        {
            return null;
        }
        return new IpcRequest
        {
            Op = IpcProtocol.JobSubmit,
            Backend = body.Backend,
            TargetAgent = body.Name ?? body.Backend + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8],
            Instruction = instruction,
            IdempotencyKey = key,
            Cwd = cwd,
            Model = body.Model,
            Effort = body.Effort,
            HerdrPlacement = body.HerdrPlacement,
            LeadSessionId = body.LeadSessionId,
            Workspace = body.Workspace
        };
    }

    static async Task<IpcRequest?> ReadLeadMessageAsync(HttpContext ctx, string leadId)
    {
        var body = await ReadBodyAsync(ctx, WebConsoleJson.Default.WebLeadMessageBody);
        if (body is not
            {
                Text: { Length: > 0 and <= 65536 } text, Workspace: { Length: > 0 and <= 4096 } workspace,
                IdempotencyKey: { Length: > 0 and <= MaxKeyChars } key
            } || !Path.IsPathFullyQualified(workspace))
        {
            return null;
        }
        return new IpcRequest
        {
            Op = IpcProtocol.ExternalOperatorSend,
            LeadSessionId = leadId,
            Workspace = workspace,
            Text = text,
            IdempotencyKey = key
        };
    }

    static async Task<IpcRequest?> ReadJoinTicketAsync(HttpContext ctx, string leadId)
    {
        var body = await ReadBodyAsync(ctx, WebConsoleJson.Default.WebJoinTicketBody);
        if (body is not { Name: { Length: > 0 and <= 64 } name, Workspace: { Length: > 0 and <= 4096 } workspace }
            || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            || !Path.IsPathFullyQualified(workspace) || body.Note is { Length: > 4096 })
        {
            return null;
        }
        return new IpcRequest
        {
            Op = IpcProtocol.ExternalTicket,
            LeadSessionId = leadId,
            Workspace = workspace,
            MemberName = name,
            Note = body.Note
        };
    }

    static async Task<T?> ReadBodyAsync<T>(HttpContext ctx, JsonTypeInfo<T> typeInfo)
    {
        var request = ctx.Request;
        if (request.ContentType is not { } type || !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
            || request.ContentLength is not (> 0 and <= MaxBodyBytes))
        {
            return default;
        }
        try { return await JsonSerializer.DeserializeAsync(request.Body, typeInfo, ctx.RequestAborted); }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException or IOException) { return default; }
    }

    static bool ValidId(string id) => id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    bool HasToken(HttpRequest request)
    {
        var header = request.Headers.Authorization;
        if (header.Count != 1 || header[0] is not { } value || !value.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value["Bearer ".Length..]), Encoding.UTF8.GetBytes(_token()));
        }
        catch (StateDirectoryException)
        {
            return false;
        }
    }

    static async Task ServeAssetAsync(HttpContext ctx, string path)
    {
        var asset = Assets.FirstOrDefault(a => a.Path == path);
        if (asset.Path is null || !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)))
        {
            await Reject(ctx, StatusCodes.Status404NotFound, NotFound);
            return;
        }

        await using var stream = typeof(WebConsoleServer).Assembly.GetManifestResourceStream("WebConsole." + asset.Resource)
            ?? throw new InvalidOperationException("missing embedded asset " + asset.Resource);
        ctx.Response.ContentType = asset.ContentType;
        ctx.Response.ContentLength = stream.Length;
        if (HttpMethods.IsGet(ctx.Request.Method))
        {
            await stream.CopyToAsync(ctx.Response.Body);
        }
    }

    static Task Reject(HttpContext ctx, int status, string code) => WriteAsync(ctx, status, new IpcResponse(false, code));

    static async Task WriteAsync(HttpContext ctx, int status, IpcResponse response)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        if (response.LeadTokens is not null && response.Page is not null)
        {
            // Web usage has explicit unknowns; ordinary IPC/MCP keeps its existing shape.
            var json = JsonSerializer.SerializeToNode(response, IpcJson.Default.IpcResponse)!;
            foreach (var job in json["page"]!["jobs"]!.AsArray())
            {
                if (!job!.AsObject().ContainsKey("session_tokens")) { job["session_tokens"] = null; }
            }
            await using var writer = new Utf8JsonWriter(ctx.Response.Body);
            json.WriteTo(writer);
            await writer.FlushAsync();
            return;
        }
        await JsonSerializer.SerializeAsync(ctx.Response.Body, response, IpcJson.Default.IpcResponse);
    }

    public async Task StopAsync() => await _app.StopAsync();

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _calls.Dispose();
    }
}
