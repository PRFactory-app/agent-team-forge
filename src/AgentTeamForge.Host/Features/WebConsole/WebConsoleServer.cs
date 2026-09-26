using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
public sealed record WebFollowUpBody(string? Instruction, string? IdempotencyKey, bool Interrupt = false);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(WebFollowUpBody))]
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
    // Room for MaxInstructionChars of any text: up to 3 UTF-8 bytes per UTF-16 char, 6 when JSON-escaped.
    public const int MaxBodyBytes = 64 * 1024;
    public const int MaxInstructionChars = 8 * 1024;
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
        var isPost = HttpMethods.IsPost(request.Method);
        if ((isPost || origin.Count > 0) && (origin.Count != 1 || !string.Equals(origin[0], _origin, StringComparison.Ordinal)))
        {
            await Reject(ctx, StatusCodes.Status403Forbidden, ForbiddenOrigin);
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
            },
            ("GET", ["jobs", var id]) when ValidId(id) => new IpcRequest { Op = IpcProtocol.JobGet, JobId = id },
            ("GET", ["jobs", var id, "output"]) when ValidId(id) => new IpcRequest
            {
                Op = IpcProtocol.JobOutput,
                JobId = id,
                Offset = long.TryParse(request.Query["offset"], out var offset) && offset >= 0 ? offset : 0,
                MaxBytes = int.TryParse(request.Query["max_bytes"], out var max) && max is > 0 and <= 65536 ? max : 65536,
            },
            ("POST", ["jobs", var id, "follow-up"]) when ValidId(id) => await ReadFollowUpAsync(ctx, id),
            ("POST", ["jobs", var id, "stop"]) when ValidId(id) => new IpcRequest { Op = IpcProtocol.JobStop, JobId = id },
            ("POST", ["jobs", var id, "stop-agent"]) when ValidId(id) => new IpcRequest { Op = IpcProtocol.JobStopAgent, JobId = id },
            _ => null,
        };
        if (ipc is null)
        {
            if (!ctx.Response.HasStarted)
            {
                await Reject(ctx, isPost ? StatusCodes.Status400BadRequest : StatusCodes.Status404NotFound, isPost ? BadRequest : NotFound);
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

    static async Task<IpcRequest?> ReadFollowUpAsync(HttpContext ctx, string jobId)
    {
        var request = ctx.Request;
        if (request.ContentType is not { } type || !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
            || request.ContentLength is not (> 0 and <= MaxBodyBytes))
        {
            return null;
        }

        WebFollowUpBody? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync(request.Body, WebConsoleJson.Default.WebFollowUpBody, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException or IOException)
        {
            return null;
        }

        if (body is not { Instruction: { Length: > 0 and <= MaxInstructionChars } instruction, IdempotencyKey: { Length: > 0 and <= MaxKeyChars } key }
            || string.IsNullOrWhiteSpace(instruction))
        {
            return null;
        }

        return new IpcRequest { Op = IpcProtocol.JobFollowUp, JobId = jobId, Instruction = instruction, IdempotencyKey = key, Interrupt = body.Interrupt };
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
        await JsonSerializer.SerializeAsync(ctx.Response.Body, response, IpcJson.Default.IpcResponse);
    }

    public async Task StopAsync() => await _app.StopAsync();

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _calls.Dispose();
    }
}
