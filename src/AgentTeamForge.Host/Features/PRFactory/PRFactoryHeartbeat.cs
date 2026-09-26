using AgentTeamForge.Host.Hosting;

namespace AgentTeamForge.Host.Features.PRFactory;

/// <summary>Opt-in machine registration/heartbeat. It never admits or claims remote work.</summary>
public static class PRFactoryHeartbeat
{
    public static async Task RunAsync(StateDirectory state, CancellationToken ct,
        Func<HttpMessageHandler>? handlerFactory = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string>? log = null)
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
                    await delay(interval, ct);
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
                    try { await delay(TimeSpan.FromSeconds(5), ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
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
