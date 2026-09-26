using System.Net.Sockets;
using System.Text;
using AgentTeamForge.Business;
using AgentTeamForge.Host.Hosting;

namespace AgentTeamForge.Host.Transport;

/// <summary>
/// Bridge/CLI side. Loads the credential from the permission-checked file and
/// sends it only inside the private hello frame. Never opens the database.
/// </summary>
/// <remarks>
/// One finite budget bounds the whole call (connect, hello, request write and
/// flush, response read); no step gets a fresh timeout. A failure before the
/// request write begins is provably unsent (<see cref="IpcProtocol.DaemonUnavailable"/>).
/// Once any request byte may have been written, the daemon may have accepted the
/// job, so every failure, deadline or caller cancellation is reported as
/// <see cref="IpcProtocol.OutcomeUnknown"/>. Recovery is the caller's explicit
/// same-key retry or job_get; this client never retries or mints a new key.
/// Caller cancellation only abandons this connection; the daemon does not tie
/// accepted work to it.
/// </remarks>
public sealed class IpcClient
{
    public static readonly TimeSpan DefaultCallBudget = TimeSpan.FromSeconds(30);
    static readonly TimeSpan MaxCallBudget = TimeSpan.FromMinutes(10);

    readonly StateDirectory _state;
    readonly SpikeLimits _limits;
    readonly TimeSpan _budget;

    public IpcClient(StateDirectory state, SpikeLimits limits, TimeSpan? callBudget = null)
    {
        var budget = callBudget ?? DefaultCallBudget;
        if (budget <= TimeSpan.Zero || budget > MaxCallBudget)
        {
            throw new ArgumentOutOfRangeException(nameof(callBudget), budget, "client call budget must be positive and finite");
        }

        _state = state;
        _limits = limits;
        _budget = budget;
    }

    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        var credential = Encoding.UTF8.GetString(StateDirectory.ReadPrivateFile(_state.CredentialFile)).Trim();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_budget);
        var requestWriteStarted = false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // ConnectAsync polls an absent pipe until the deadline; a missing daemon must fail fast.
                if (!await WindowsPipe.AppearsAsync(() => WindowsPipe.Exists(_state.Socket), TimeSpan.FromMilliseconds(250), deadline.Token))
                {
                    return new IpcResponse(false, IpcProtocol.DaemonUnavailable);
                }
                await using var pipe = await WindowsPipe.ConnectAsync(_state.Socket, deadline.Token);
                using var abort = deadline.Token.Register(pipe.Dispose);
                return await ExchangeAsync(pipe);
            }
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var socketAbort = deadline.Token.Register(socket.Dispose);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_state.Socket), deadline.Token);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            return await ExchangeAsync(stream);

            async Task<IpcResponse> ExchangeAsync(Stream connection)
            {
                await Frames.WriteAsync(connection, new IpcRequest { ProtocolVersion = IpcProtocol.Version, Op = IpcProtocol.Hello, Credential = credential },
                    IpcJson.Default.IpcRequest, deadline.Token);
                var hello = await Frames.ReadAsync(connection, IpcJson.Default.IpcResponse, _limits.MaxFrameBytes, Timeout.InfiniteTimeSpan, deadline.Token);
                if (hello is null || !hello.Ok)
                {
                    return hello ?? new IpcResponse(false, IpcProtocol.DaemonUnavailable);
                }

                requestWriteStarted = true;
                await Frames.WriteAsync(connection, request with { ProtocolVersion = request.ProtocolVersion == 0 ? IpcProtocol.Version : request.ProtocolVersion },
                    IpcJson.Default.IpcRequest, deadline.Token);
                return await Frames.ReadAsync(connection, IpcJson.Default.IpcResponse, _limits.MaxFrameBytes, Timeout.InfiniteTimeSpan, deadline.Token)
                    ?? new IpcResponse(false, IpcProtocol.OutcomeUnknown);
            }
        }
        catch (Exception ex) when (requestWriteStarted && ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or FrameException)
        {
            return new IpcResponse(false, IpcProtocol.OutcomeUnknown);
        }
        catch (UnauthorizedAccessException)
        {
            return requestWriteStarted ? new IpcResponse(false, IpcProtocol.OutcomeUnknown)
                : new IpcResponse(false, IpcProtocol.AccessDenied, ErrorDetail: AccessDeniedDetail);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            return requestWriteStarted ? new IpcResponse(false, IpcProtocol.OutcomeUnknown)
                : new IpcResponse(false, IpcProtocol.AccessDenied, ErrorDetail: AccessDeniedDetail);
        }
        catch (FrameException ex)
        {
            return new IpcResponse(false, ex.Code);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new IpcResponse(false, IpcProtocol.DaemonUnavailable);
        }
    }

    internal const string AccessDeniedDetail = "Access to the daemon endpoint was denied. Check its owner and permissions.";
}
