using AgentTeamForge.Host.Hosting;

namespace AgentTeamForge.Host.Features.PRFactory;

/// <summary>Opt-in machine registration/heartbeat and connected work-item tick.</summary>
public static class PRFactoryHeartbeat
{
    static readonly TimeSpan FastTick = TimeSpan.FromSeconds(2);

    public static async Task RunAsync(StateDirectory state, CancellationToken ct,
        Func<HttpMessageHandler>? handlerFactory = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string>? log = null,
        Func<PRFactoryClient, PRFactorySettings, Guid, CancellationToken, Task>? onConnected = null,
        Func<CancellationToken, Task>? onTokenRejected = null, Func<bool>? activeWork = null,
        Func<CancellationToken, Task>? onUnavailable = null)
    {
        delay ??= Task.Delay;
        log ??= _ => { };
        string? connection = null;
        HttpClient? http = null;
        PRFactoryClient? client = null;
        Guid? machineId = null;
        var interval = TimeSpan.FromSeconds(30);
        var failures = 0;
        var rejected = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var settings = PRFactoryConnection.LoadSettings(state);
                    if (settings is null)
                    {
                        if (onUnavailable is not null) { await onUnavailable(ct); }
                        http?.Dispose();
                        http = null;
                        client = null;
                        connection = null;
                        machineId = null;
                        await delay(TimeSpan.FromSeconds(5), ct);
                        continue;
                    }
                    var token = PRFactoryConnection.ReadToken(state);
                    var key = settings.Url + "\n" + token;
                    if (key != connection)
                    {
                        http?.Dispose();
                        http = PRFactoryClient.CreateHttpClient(settings.Url, token, handlerFactory?.Invoke());
                        client = new PRFactoryClient(http);
                        connection = key;
                        machineId = null;
                        failures = 0;
                        rejected = PRFactoryConnection.IsRejected(state);
                    }
                    if (rejected && !PRFactoryConnection.IsRejected(state))
                    {
                        rejected = false; // An explicit reconnect re-enables registration.
                        machineId = null;
                    }
                    if (rejected)
                    {
                        if (onTokenRejected is not null) { await onTokenRejected(ct); }
                        await delay(TimeSpan.FromSeconds(5), ct);
                        continue;
                    }
                    if (machineId is null)
                    {
                        var registration = await client!.RegisterMachineAsync(ct);
                        machineId = registration.MachineId;
                        interval = TimeSpan.FromSeconds(Math.Clamp(registration.HeartbeatIntervalSeconds, 1, 3600));
                        log($"PRFactory machine registered: {machineId}");
                    }
                    else if (!await client!.HeartbeatMachineAsync(machineId.Value, ct))
                    {
                        machineId = null; // Server forgot this registration: register again.
                        throw new HttpRequestException("PRFactory machine registration expired");
                    }
                    failures = 0;
                    if (onConnected is not null)
                    {
                        await onConnected(client!, settings, machineId!.Value, ct);
                    }

                    // While teams are active, commands/answers/waits tick every ~2 s between machine heartbeats.
                    var remaining = interval;
                    while (onConnected is not null && activeWork?.Invoke() == true && remaining > FastTick)
                    {
                        await delay(FastTick, ct);
                        remaining -= FastTick;
                        if (!StillConfigured(state, connection))
                        {
                            if (onUnavailable is not null) { await onUnavailable(ct); }
                            remaining = TimeSpan.Zero;
                            break;
                        }
                        await onConnected(client!, settings, machineId!.Value, ct);
                    }
                    await delay(remaining, ct);
                }
                catch (WorkerTokenRejectedException)
                {
                    rejected = true;
                    // A reconnect may have replaced the token while this request was in flight;
                    // only the token the server actually rejected is marked.
                    if (StillConfigured(state, connection))
                    {
                        PRFactoryConnection.MarkRejected(state);
                    }
                    log("PRFactory worker token rejected; remote connector stopped");
                    try
                    {
                        // Quiesce owned work without erasing acceptance identity.
                        if (onTokenRejected is not null) { await onTokenRejected(ct); }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException && !(ex is OperationCanceledException && ct.IsCancellationRequested))
                    {
                        log($"PRFactory token-rejection fencing deferred ({ex.GetType().Name})");
                    }
                    try { await delay(TimeSpan.FromSeconds(5), ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (onUnavailable is not null) { await onUnavailable(ct); }
                    failures = Math.Min(failures + 1, 6);
                    var wait = TimeSpan.FromSeconds(Math.Min(60, 1 << (failures - 1)));
                    log($"PRFactory heartbeat failed ({ex.GetType().Name}); retry in {wait.TotalSeconds}s");
                    try { await delay(wait, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                }
            }
        }
        finally { http?.Dispose(); }
    }

    static bool StillConfigured(StateDirectory state, string? connection)
    {
        try
        {
            var settings = PRFactoryConnection.LoadSettings(state);
            return settings is not null && settings.Url + "\n" + PRFactoryConnection.ReadToken(state) == connection;
        }
        catch (Exception ex) when (ex is IOException or StateDirectoryException) { return false; }
    }
}
