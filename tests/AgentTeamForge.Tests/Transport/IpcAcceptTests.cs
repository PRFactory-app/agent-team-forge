using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using AgentTeamForge.Business;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Transport;

public sealed class IpcAcceptTests
{
    [Fact]
    public async Task Failed_accept_and_connection_handler_do_not_stop_shared_loop()
    {
        using var state = new TempStateDir();
        using var lifetime = new CancellationTokenSource();
        var messages = new ConcurrentQueue<string>();
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var connections = 0;

        async Task<MemoryStream> Accept(CancellationToken ct)
        {
            switch (Interlocked.Increment(ref attempts))
            {
                case 1: throw new IOException("pipe closed before accept");
                case 2: throw new SocketException((int)SocketError.ConnectionAborted);
                case 3:
                case 4: return new MemoryStream();
                default:
                    await Task.Delay(Timeout.Infinite, ct);
                    throw new InvalidOperationException("unreachable");
            }
        }

        Task Handle(MemoryStream _)
        {
            if (Interlocked.Increment(ref connections) == 1)
            {
                throw new InvalidOperationException("handler failed");
            }
            handled.TrySetResult();
            return Task.CompletedTask;
        }

        // The injected accept and handler use the same recovery path as both
        // real transports, including the Windows pipe's ERROR_NO_DATA case.
        using var loggedServer = new IpcServer(state.Path, [], new BoundPrincipal("op", "team", "agent"),
            new SpikeLimits(), _ => new IpcResponse(true), messages.Enqueue);
        var serving = loggedServer.ServeAcceptedAsync(Accept, Handle, lifetime.Token);
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await lifetime.CancelAsync();
        await serving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Bounded.Until(() => messages.Count == 3, "accept and handler failures logged");

        Assert.True(attempts >= 4);
        Assert.Equal(2, connections);
        Assert.Equal(2, messages.Count(message => message.Contains("ipc accept failed", StringComparison.Ordinal)));
        Assert.Contains(messages, message => message.Contains("ipc connection failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Immediate_unix_disconnects_leave_normal_requests_available()
    {
        if (OperatingSystem.IsWindows()) { return; }

        using var state = new TempStateDir();
        const string credential = "test-credential";
        var credentialFile = state.File("operator.key");
        File.WriteAllText(credentialFile, credential);
        File.SetUnixFileMode(credentialFile, StateDirectory.PrivateFile);
        var dir = StateDirectory.Open(state.Path);
        using var server = new IpcServer(dir.Socket, Encoding.UTF8.GetBytes(credential),
            new BoundPrincipal("op", "team", "agent"), new SpikeLimits(),
            _ => new IpcResponse(true), _ => { });
        using var lifetime = new CancellationTokenSource();
        using var listener = server.Bind();
        var serving = server.ServeAsync(listener, lifetime.Token);
        var client = new IpcClient(dir, new SpikeLimits(), TimeSpan.FromSeconds(5));
        var request = new IpcRequest { Op = IpcProtocol.JobList };

        for (var i = 0; i < 200; i++)
        {
            using (var raw = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                await raw.ConnectAsync(new UnixDomainSocketEndPoint(dir.Socket), TestContext.Current.CancellationToken);
            }
            if (i % 10 == 0)
            {
                Assert.True((await client.SendAsync(request, TestContext.Current.CancellationToken)).Ok);
                Assert.False(serving.IsCompleted);
            }
        }

        Assert.True((await client.SendAsync(request, TestContext.Current.CancellationToken)).Ok);
        await lifetime.CancelAsync();
        listener.Dispose();
        await serving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
