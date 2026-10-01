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
/// At most <see cref="MaxConcurrentConnections"/> requests are handled at once. Further authenticated
/// connections wait for a slot; one still waiting after <see cref="SlotWait"/> is answered
/// <see cref="IpcProtocol.DaemonBusy"/> in place of the hello acknowledgement, before any request
/// byte is sent, so the client can retry it safely. A live daemon never just drops a connection.
/// Before authentication a connection is bounded separately: its hello may be at most <see cref="MaxHelloBytes"/>,
/// and at most <see cref="MaxPendingHandshakes"/> hellos are awaited at once. A connection beyond that is
/// answered busy as soon as its hello arrives (or <see cref="SlotWait"/> passes), without authenticating it.
/// </summary>
public sealed class IpcServer(string socketPath, byte[] credential, BoundPrincipal principal, SpikeLimits limits,
    Func<IpcRequest, IpcResponse> handle, Action<string> log, Action<IpcResponse>? afterReply = null,
    Func<IpcRequest, IDisposable?>? beforeRequest = null) : IDisposable
{
    internal const int MaxConcurrentConnections = 16;
    internal static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(1);
    internal const int MaxPendingHandshakes = 64;

    // A hello is the protocol version, op, operator credential and optional principal: well under this.
    internal const int MaxHelloBytes = 16 * 1024;
    readonly SemaphoreSlim _slots = new(MaxConcurrentConnections);
    readonly SemaphoreSlim _handshakes = new(MaxPendingHandshakes);

    public void Dispose()
    {
        _slots.Dispose();
        _handshakes.Dispose();
    }

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
        // Accepting no longer waits for a free handler, so the backlog only absorbs connect bursts.
        socket.Listen(128);
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
            // Concurrency is limited in HandleStreamAsync, for pending hellos and per request after the hello,
            // so a burst of clients waits or gets a retryable busy reply instead of a silently closed connection.
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
                    }
                }, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                connection.Dispose();
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
        var slotHeld = false;
        var handshakeHeld = false;
        try
        {
            if (!peerAuthorized)
            {
                return;
            }

            if (!(handshakeHeld = _handshakes.Wait(0, CancellationToken.None)))
            {
                await AnswerBusyAsync(stream, daemonLifetime);
                return;
            }

            var hello = await Frames.ReadAsync(stream, IpcJson.Default.IpcRequest, MaxHelloBytes, limits.FrameReadTimeout, daemonLifetime);
            _handshakes.Release();
            handshakeHeld = false;
            if (hello is null)
            {
                return;
            }

            var rejection = Authenticate(hello);
            if (rejection is null)
            {
                slotHeld = await _slots.WaitAsync(SlotWait, daemonLifetime);
                rejection = slotHeld ? null : new IpcResponse(false, IpcProtocol.DaemonBusy);
            }
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
        finally
        {
            if (handshakeHeld) { _handshakes.Release(); }
            if (slotHeld) { _slots.Release(); }
        }
    }

    // The hello is consumed first, as on the authenticated busy path, so the client's hello write never meets a
    // closed peer and it reads a retryable busy reply. The hello itself is never authenticated here.
    static async Task AnswerBusyAsync(Stream stream, CancellationToken daemonLifetime)
    {
        try
        {
            if (await Frames.ReadAsync(stream, IpcJson.Default.IpcRequest, MaxHelloBytes, SlotWait, daemonLifetime) is null)
            {
                return;
            }
        }
        catch (OperationCanceledException) when (!daemonLifetime.IsCancellationRequested)
        {
            // A hello still unsent after the wait is answered busy too.
        }
        await Frames.WriteAsync(stream, new IpcResponse(false, IpcProtocol.DaemonBusy), IpcJson.Default.IpcResponse, daemonLifetime);
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
