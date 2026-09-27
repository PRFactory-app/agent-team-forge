using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AgentTeamForge.Business;
using AgentTeamForge.Host.Hosting;

namespace AgentTeamForge.Host.Transport;

/// <summary>
/// Owner-private Unix socket on Linux, same-user named pipe on Windows. Each
/// connection: OS peer restriction, one hello frame with the operator credential,
/// then one request frame and one response.
/// Handlers run against the daemon lifetime, not the client's connection.
/// </summary>
public sealed class IpcServer(string socketPath, byte[] credential, BoundPrincipal principal, SpikeLimits limits,
    Func<IpcRequest, IpcResponse> handle, Action<string> log, Action<IpcResponse>? afterReply = null,
    Func<IpcRequest, IDisposable?>? beforeRequest = null) : IDisposable
{
    const int MaxConcurrentConnections = 16;
    readonly SemaphoreSlim _slots = new(MaxConcurrentConnections);

    public void Dispose() => _slots.Dispose();

    /// <summary>Caller must already hold the daemon lock; only then is a stale socket unlinked.</summary>
    public Socket Bind()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Unix socket binding is Linux-only");
        }

        if (File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));
        File.SetUnixFileMode(socketPath, StateDirectory.PrivateFile);
        socket.Listen(32);
        return socket;
    }

    public async Task ServeAsync(Socket listener, CancellationToken daemonLifetime)
    {
        await ServeAcceptedAsync(ct => listener.AcceptAsync(ct).AsTask(),
            client => HandleConnectionAsync(client, daemonLifetime), daemonLifetime);
    }

    /// <summary>Windows transport: same-user pipe plus the existing credential hello.</summary>
    [SupportedOSPlatform("windows")]
    public async Task ServeWindowsAsync(CancellationToken daemonLifetime)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("named pipe transport requires Windows");
        }

        await ServeAcceptedAsync(async ct =>
        {
            var pipe = WindowsPipe.CreateServer(socketPath);
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                return pipe;
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }, pipe => HandleStreamAsync(pipe, peerAuthorized: true, daemonLifetime), daemonLifetime);
    }

    // Both transports use this loop. A failed accept owns no connection; a
    // failed handler owns only its connection, never the serving task.
    // A squatted pipe, a disposed listener or a long unbroken failure streak is
    // fatal: the serving task faults and the daemon exits unhealthy.
    internal async Task ServeAcceptedAsync<T>(Func<CancellationToken, Task<T>> accept,
        Func<T, Task> handleConnection, CancellationToken daemonLifetime, int maxConsecutiveFailures = 60) where T : IDisposable
    {
        var retryDelay = 25;
        var failures = 0;
        var lastFullLog = DateTime.MinValue;
        while (!daemonLifetime.IsCancellationRequested)
        {
            T connection;
            try
            {
                connection = await accept(daemonLifetime);
            }
            catch (Exception) when (daemonLifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or UnauthorizedAccessException or ObjectDisposedException))
            {
                if (++failures >= maxConsecutiveFailures)
                {
                    throw new IOException($"ipc accept failed {failures} times in a row", ex);
                }
                // Full stack at most once a minute; a hostile or broken client
                // must not fill daemon.log.
                if (DateTime.UtcNow - lastFullLog >= TimeSpan.FromMinutes(1))
                {
                    lastFullLog = DateTime.UtcNow;
                    log($"ipc accept failed: {ex}");
                }
                else
                {
                    log($"ipc accept failed: {ex.GetType().Name}: {ex.Message}");
                }
                try { await Task.Delay(retryDelay, daemonLifetime); }
                catch (OperationCanceledException) when (daemonLifetime.IsCancellationRequested) { return; }
                retryDelay = Math.Min(retryDelay * 2, 1000);
                continue;
            }

            retryDelay = 25;
            failures = 0;
            if (!_slots.Wait(0, CancellationToken.None))
            {
                connection.Dispose();
                continue;
            }

            try
            {
                _ = Task.Run(async () =>
                {
                    try { await handleConnection(connection); }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        log($"ipc connection failed: {ex}");
                    }
                    finally
                    {
                        connection.Dispose();
                        _slots.Release();
                    }
                }, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                connection.Dispose();
                _slots.Release();
                log($"ipc connection launch failed: {ex}");
            }
        }
    }

    async Task HandleConnectionAsync(Socket client, CancellationToken daemonLifetime)
    {
        await using var stream = new NetworkStream(client, ownsSocket: false);
        await HandleStreamAsync(stream, peerAuthorized: PeerUid(client) == Native.geteuid(), daemonLifetime);
    }

    async Task HandleStreamAsync(Stream stream, bool peerAuthorized, CancellationToken daemonLifetime)
    {
        try
        {
            if (!peerAuthorized)
            {
                return;
            }

            var hello = await Frames.ReadAsync(stream, IpcJson.Default.IpcRequest, limits.MaxFrameBytes, limits.FrameReadTimeout, daemonLifetime);
            if (hello is null)
            {
                return;
            }

            var rejection = Authenticate(hello);
            await Frames.WriteAsync(stream, rejection ?? new IpcResponse(true), IpcJson.Default.IpcResponse, daemonLifetime);
            if (rejection is not null)
            {
                return;
            }

            var request = await Frames.ReadAsync(stream, IpcJson.Default.IpcRequest, limits.MaxFrameBytes, limits.FrameReadTimeout, daemonLifetime);
            if (request is null)
            {
                return;
            }

            IDisposable? claimPause;
            try { claimPause = beforeRequest?.Invoke(request); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log($"request failed: {ex}");
                await TryWriteAsync(stream, new IpcResponse(false, IpcProtocol.InternalError));
                return;
            }
            using var claim = claimPause;

            // Business work is synchronous and not tied to this connection;
            // a vanished client cannot cancel a committed job.
            var response = request.ProtocolVersion != IpcProtocol.Version
                ? new IpcResponse(false, IpcProtocol.UnsupportedVersion)
                : HandleRequest(request);
            try
            {
                await Frames.WriteAsync(stream, response, IpcJson.Default.IpcResponse, daemonLifetime);
            }
            finally
            {
                afterReply?.Invoke(response);
            }
        }
        catch (FrameException ex)
        {
            await TryWriteAsync(stream, new IpcResponse(false, ex.Code));
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex)
        {
            log($"client connection dropped: {ex}");
        }
        catch (SocketException ex)
        {
            log($"client connection dropped: {ex}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // E.g. a storage or injected failure: report no acceptance, keep serving.
            log($"request failed: {ex}");
            await TryWriteAsync(stream, new IpcResponse(false, IpcProtocol.InternalError));
        }
    }

    // A handler's own IOException (e.g. file access) must not look like a dropped client.
    IpcResponse HandleRequest(IpcRequest request)
    {
        try
        {
            return handle(request);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            log($"request failed: {ex}");
            return new IpcResponse(false, IpcProtocol.InternalError);
        }
    }

    IpcResponse? Authenticate(IpcRequest hello)
    {
        if (hello.Op != IpcProtocol.Hello || hello.ProtocolVersion != IpcProtocol.Version)
        {
            return new IpcResponse(false, IpcProtocol.UnsupportedVersion);
        }

        var presented = Encoding.UTF8.GetBytes(hello.Credential ?? string.Empty);
        if (!CryptographicOperations.FixedTimeEquals(presented, credential))
        {
            return new IpcResponse(false, IpcProtocol.Unauthenticated);
        }

        return hello.Principal is not null && hello.Principal != principal.Principal
            ? new IpcResponse(false, IpcProtocol.IdentityMismatch)
            : null;
    }

    static async Task TryWriteAsync(Stream stream, IpcResponse response)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Frames.WriteAsync(stream, response, IpcJson.Default.IpcResponse, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
        }
    }

    /// <summary>SO_PEERCRED: struct ucred { pid_t pid; uid_t uid; gid_t gid; }.</summary>
    static uint PeerUid(Socket socket)
    {
        if (OperatingSystem.IsMacOS())
        {
            return Native.GetPeerEid(socket.SafeHandle.DangerousGetHandle(), out var uid, out _) == 0 ? uid : uint.MaxValue;
        }

        Span<byte> ucred = stackalloc byte[12];
        var length = socket.GetRawSocketOption(1, 17, ucred);
        return length == 12 ? BitConverter.ToUInt32(ucred[4..8]) : uint.MaxValue;
    }
}
