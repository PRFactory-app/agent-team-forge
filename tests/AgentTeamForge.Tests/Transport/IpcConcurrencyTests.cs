using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using AgentTeamForge.Business;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Transport;

/// <summary>
/// A live daemon with every request slot taken must make callers wait or answer a retryable
/// daemon_busy before any request byte is sent; it must never look like daemon_unavailable.
/// </summary>
public sealed class IpcConcurrencyTests
{
    const string Credential = "test-credential";
    static readonly IpcRequest List = new() { Op = IpcProtocol.JobList };

    [Fact]
    public async Task Clients_beyond_the_handler_slots_wait_for_one_instead_of_failing()
    {
        if (OperatingSystem.IsWindows()) { return; }

        using var state = new TempStateDir();
        var dir = Private(state);
        var running = 0;
        var peak = 0;
        var handled = 0;
        using var server = Server(dir, _ =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            Thread.Sleep(50);
            Interlocked.Decrement(ref running);
            Interlocked.Increment(ref handled);
            return new IpcResponse(true);
        });
        using var lifetime = new CancellationTokenSource();
        using var listener = server.Bind();
        var serving = server.ServeAsync(listener, lifetime.Token);

        const int clients = 3 * IpcServer.MaxConcurrentConnections + 2;
        var responses = await Task.WhenAll(Enumerable.Range(0, clients).Select(_ =>
            Task.Run(() => new IpcClient(dir, new SpikeLimits(), TimeSpan.FromSeconds(30)).SendAsync(List, CancellationToken.None))));

        Assert.All(responses, response => Assert.True(response.Ok, response.Error));
        Assert.Equal(clients, handled);
        Assert.InRange(peak, 1, IpcServer.MaxConcurrentConnections);
        await lifetime.CancelAsync();
        listener.Dispose();
        await serving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Slots_held_past_the_wait_answer_busy_before_the_request_and_never_unavailable()
    {
        if (OperatingSystem.IsWindows()) { return; }

        using var state = new TempStateDir();
        var dir = Private(state);
        // Each held slot blocks a pool thread in the synchronous handler; do not wait on thread injection.
        using var threads = new MinimumThreads(4 * IpcServer.MaxConcurrentConnections);
        using var release = new ManualResetEventSlim();
        var entered = 0;
        using var server = Server(dir, _ =>
        {
            Interlocked.Increment(ref entered);
            release.Wait(TimeSpan.FromSeconds(30));
            return new IpcResponse(true);
        });
        using var lifetime = new CancellationTokenSource();
        using var listener = server.Bind();
        var serving = server.ServeAsync(listener, lifetime.Token);

        var holders = Enumerable.Range(0, IpcServer.MaxConcurrentConnections).Select(_ =>
            Task.Run(() => new IpcClient(dir, new SpikeLimits(), TimeSpan.FromSeconds(60)).SendAsync(List, CancellationToken.None))).ToArray();
        await Bounded.Until(() => Volatile.Read(ref entered) == IpcServer.MaxConcurrentConnections, "every slot held");

        var budget = IpcServer.SlotWait * 2.5;
        var watch = Stopwatch.StartNew();
        var busy = await new IpcClient(dir, new SpikeLimits(), budget).SendAsync(List, TestContext.Current.CancellationToken);
        watch.Stop();

        Assert.Equal(IpcProtocol.DaemonBusy, busy.Error);
        Assert.True(watch.Elapsed < budget + TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
        Assert.Equal(IpcServer.MaxConcurrentConnections, Volatile.Read(ref entered)); // the busy request never reached a handler

        release.Set();
        Assert.All(await Task.WhenAll(holders), response => Assert.True(response.Ok, response.Error));
        Assert.True((await new IpcClient(dir, new SpikeLimits(), TimeSpan.FromSeconds(5)).SendAsync(List, TestContext.Current.CancellationToken)).Ok);
        await lifetime.CancelAsync();
        listener.Dispose();
        await serving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Oversized_hello_is_refused_from_its_header_before_authentication()
    {
        if (OperatingSystem.IsWindows()) { return; }

        using var state = new TempStateDir();
        var dir = Private(state);
        using var server = Server(dir, _ => new IpcResponse(true));
        using var lifetime = new CancellationTokenSource();
        using var listener = server.Bind();
        var serving = server.ServeAsync(listener, lifetime.Token);

        using var socket = await Connect(dir);
        await using var stream = new NetworkStream(socket);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, IpcServer.MaxHelloBytes + 1);
        await stream.WriteAsync(header, TestContext.Current.CancellationToken);
        var reply = await Frames.ReadAsync(stream, IpcJson.Default.IpcResponse, 4096, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal(IpcProtocol.FrameTooLarge, reply?.Error);
        await lifetime.CancelAsync();
        listener.Dispose();
        await serving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Pending_handshakes_beyond_the_cap_are_answered_busy_and_never_unavailable()
    {
        if (OperatingSystem.IsWindows()) { return; }

        using var state = new TempStateDir();
        var dir = Private(state);
        // Silent connections hold their handshake for the whole frame read timeout.
        using var server = Server(dir, _ => new IpcResponse(true), new SpikeLimits { FrameReadTimeout = TimeSpan.FromSeconds(60) });
        using var lifetime = new CancellationTokenSource();
        using var listener = server.Bind();
        var serving = server.ServeAsync(listener, lifetime.Token);

        var silent = new List<Socket>();
        try
        {
            for (var i = 0; i < IpcServer.MaxPendingHandshakes; i++) { silent.Add(await Connect(dir)); }
            await Bounded.Until(async () => await Hello(dir) is { Error: IpcProtocol.DaemonBusy } busy ? busy : null,
                "a hello beyond the handshake cap answered busy");

            var response = await new IpcClient(dir, new SpikeLimits(), IpcServer.SlotWait * 2).SendAsync(List, TestContext.Current.CancellationToken);
            Assert.Equal(IpcProtocol.DaemonBusy, response.Error);
        }
        finally
        {
            silent.ForEach(socket => socket.Dispose());
        }

        // Closed silent connections free their handshakes.
        Assert.True((await new IpcClient(dir, new SpikeLimits(), TimeSpan.FromSeconds(10)).SendAsync(List, TestContext.Current.CancellationToken)).Ok);
        await lifetime.CancelAsync();
        listener.Dispose();
        await serving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    static async Task<Socket> Connect(StateDirectory dir)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(dir.Socket), TestContext.Current.CancellationToken);
        return socket;
    }

    static async Task<IpcResponse?> Hello(StateDirectory dir)
    {
        using var socket = await Connect(dir);
        await using var stream = new NetworkStream(socket);
        await Frames.WriteAsync(stream, new IpcRequest { ProtocolVersion = IpcProtocol.Version, Op = IpcProtocol.Hello, Credential = Credential },
            IpcJson.Default.IpcRequest, TestContext.Current.CancellationToken);
        return await Frames.ReadAsync(stream, IpcJson.Default.IpcResponse, 4096, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    static StateDirectory Private(TempStateDir state)
    {
        var path = state.File("operator.key");
        File.WriteAllText(path, Credential);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, StateDirectory.PrivateFile); }
        return StateDirectory.Open(state.Path);
    }

    static IpcServer Server(StateDirectory dir, Func<IpcRequest, IpcResponse> handle, SpikeLimits? limits = null) =>
        new(dir.Socket, Encoding.UTF8.GetBytes(Credential), new BoundPrincipal("op", "team", "agent"), limits ?? new SpikeLimits(), handle, _ => { });

    sealed class MinimumThreads : IDisposable
    {
        readonly int _workers;
        readonly int _io;

        public MinimumThreads(int workers)
        {
            ThreadPool.GetMinThreads(out _workers, out _io);
            ThreadPool.SetMinThreads(Math.Max(workers, _workers), _io);
        }

        public void Dispose() => ThreadPool.SetMinThreads(_workers, _io);
    }

    static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current) { return; }
            current = seen;
        }
    }
}
