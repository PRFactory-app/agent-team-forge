using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AgentTeamForge.Business;
using AgentTeamForge.Host.Hosting;

namespace AgentTeamForge.Host.Transport;

/// <summary>
/// Owner-private Unix socket. Each connection: peer-UID check, one hello frame
/// with the operator credential, then one request frame and one response.
/// Handlers run against the daemon lifetime, not the client's connection.
/// </summary>
public sealed class IpcServer(string socketPath, byte[] credential, BoundPrincipal principal, SpikeLimits limits,
    Func<IpcRequest, IpcResponse> handle, Action<string> log) : IDisposable
{
    const int MaxConcurrentConnections = 16;
    readonly SemaphoreSlim _slots = new(MaxConcurrentConnections);

    public void Dispose() => _slots.Dispose();

    /// <summary>Caller must already hold the daemon lock; only then is a stale socket unlinked.</summary>
    public Socket Bind()
    {
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
        while (!daemonLifetime.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(daemonLifetime);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_slots.Wait(0, CancellationToken.None))
            {
                client.Dispose();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleConnectionAsync(client, daemonLifetime);
                }
                finally
                {
                    client.Dispose();
                    _slots.Release();
                }
            }, CancellationToken.None);
        }
    }

    async Task HandleConnectionAsync(Socket client, CancellationToken daemonLifetime)
    {
        await using var stream = new NetworkStream(client, ownsSocket: false);
        try
        {
            if (PeerUid(client) != Native.geteuid())
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

            // Business work is synchronous and not tied to this connection;
            // a vanished client cannot cancel a committed job.
            var response = request.ProtocolVersion != IpcProtocol.Version
                ? new IpcResponse(false, IpcProtocol.UnsupportedVersion)
                : handle(request);
            await Frames.WriteAsync(stream, response, IpcJson.Default.IpcResponse, daemonLifetime);
        }
        catch (FrameException ex)
        {
            await TryWriteAsync(stream, new IpcResponse(false, ex.Code));
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            log("client connection dropped");
        }
        catch (SocketException)
        {
            log("client connection dropped");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // E.g. a storage or injected failure: report no acceptance, keep serving.
            log($"request failed: {ex.GetType().Name}");
            await TryWriteAsync(stream, new IpcResponse(false, IpcProtocol.InternalError));
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
        Span<byte> ucred = stackalloc byte[12];
        var length = socket.GetRawSocketOption(1, 17, ucred);
        return length == 12 ? BitConverter.ToUInt32(ucred[4..8]) : uint.MaxValue;
    }
}
