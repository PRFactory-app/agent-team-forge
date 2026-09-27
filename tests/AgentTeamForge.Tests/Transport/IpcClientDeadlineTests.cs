using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using AgentTeamForge.Business;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Transport;

/// <summary>
/// One finite total budget covers the whole client call. A failure before any
/// request byte could have been written is provably unsent (daemon_unavailable);
/// anything after that is outcome_unknown. The client never retries by itself.
/// </summary>
public sealed class IpcClientDeadlineTests
{
    static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(800);

    // Generous upper bound for scheduling noise; far below the old 5 s/30 s step timeouts.
    static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    static readonly IpcRequest Submit = new() { Op = IpcProtocol.JobSubmit, IdempotencyKey = "k", Instruction = "x" };

    [Fact]
    public async Task Absent_windows_pipe_is_detected_after_a_short_grace_not_the_budget()
    {
        var watch = Stopwatch.StartNew();
        Assert.False(await WindowsPipe.AppearsAsync(() => false, TimeSpan.FromMilliseconds(100), CancellationToken.None));
        Assert.True(watch.Elapsed < Slack);
        var probes = 0;
        Assert.True(await WindowsPipe.AppearsAsync(() => ++probes > 2, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task Peer_that_never_accepts_is_bounded_and_provably_unsent()
    {
        using var peer = new FakePeer(accept: false);

        var (response, elapsed) = await Timed(() => peer.Client(Budget).SendAsync(Submit, CancellationToken.None));

        Assert.Equal(IpcProtocol.DaemonUnavailable, response.Error);
        Assert.True(elapsed < Budget + Slack, $"took {elapsed}");
    }

    [Fact]
    public async Task Peer_that_never_answers_hello_is_bounded_and_provably_unsent()
    {
        using var peer = new FakePeer(async (stream, ct) =>
        {
            await Frames.ReadAsync(stream, IpcJson.Default.IpcRequest, 1 << 20, Timeout.InfiniteTimeSpan, ct);
            await Task.Delay(Timeout.Infinite, ct);
        });

        var (response, elapsed) = await Timed(() => peer.Client(Budget).SendAsync(Submit, CancellationToken.None));

        Assert.Equal(IpcProtocol.DaemonUnavailable, response.Error);
        Assert.True(elapsed < Budget + Slack, $"took {elapsed}");
    }

    [Fact]
    public async Task Stalled_response_after_request_write_is_outcome_unknown()
    {
        using var peer = new FakePeer(async (stream, ct) =>
        {
            await FakePeer.AnswerHelloAsync(stream, ct);
            await ReadRequestAsync(stream, ct);
            await Task.Delay(Timeout.Infinite, ct);
        });

        var (response, elapsed) = await Timed(() => peer.Client(Budget).SendAsync(Submit, CancellationToken.None));

        Assert.Equal(IpcProtocol.OutcomeUnknown, response.Error);
        Assert.True(elapsed < Budget + Slack, $"took {elapsed}");
        Assert.Equal(1, peer.Connections);
    }

    [Fact]
    public async Task Stalled_request_write_is_bounded_and_outcome_unknown()
    {
        // The peer stops reading after hello; a request larger than the socket
        // buffers blocks the client's write part-way through.
        using var peer = new FakePeer(async (stream, ct) =>
        {
            await FakePeer.AnswerHelloAsync(stream, ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        var big = Submit with { Instruction = new string('x', 8 * 1024 * 1024) };

        var (response, elapsed) = await Timed(() => peer.Client(Budget).SendAsync(big, CancellationToken.None));

        Assert.Equal(IpcProtocol.OutcomeUnknown, response.Error);
        Assert.True(elapsed < Budget + Slack, $"took {elapsed}");
        Assert.Equal(1, peer.Connections);
    }

    [Fact]
    public async Task Budget_is_total_and_not_reset_per_step()
    {
        var clock = new ManualTimeProvider();
        var helloRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendHello = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var peer = new FakePeer(async (stream, ct) =>
        {
            await Frames.ReadAsync(stream, IpcJson.Default.IpcRequest, 1 << 20, Timeout.InfiniteTimeSpan, ct);
            helloRead.SetResult();
            await sendHello.Task.WaitAsync(ct);
            await Frames.WriteAsync(stream, new IpcResponse(true), IpcJson.Default.IpcResponse, ct);
            await ReadRequestAsync(stream, ct);
            requestRead.SetResult();
            await sendResponse.Task.WaitAsync(ct);
            await Frames.WriteAsync(stream, new IpcResponse(true, Outcome: "accepted"), IpcJson.Default.IpcResponse, ct);
        });

        var call = new IpcClient(peer.State, new SpikeLimits(), Budget, clock).SendAsync(Submit, CancellationToken.None);
        await helloRead.Task.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
        clock.Advance(Budget * 0.7);
        sendHello.SetResult();
        await requestRead.Task.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
        clock.Advance(Budget * 0.4);
        sendResponse.SetResult();
        var response = await call.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);

        Assert.Equal(IpcProtocol.OutcomeUnknown, response.Error);
    }

    [Fact]
    public async Task Caller_cancellation_before_any_write_is_provably_unsent()
    {
        using var peer = new FakePeer(accept: false);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => peer.Client(TimeSpan.FromSeconds(30)).SendAsync(Submit, caller.Token));
    }

    [Fact]
    public async Task Caller_cancellation_after_request_write_is_outcome_unknown_and_does_not_cancel_accepted_job()
    {
        using var state = new TempStateDir();
        var credential = FakePeer.WriteCredential(state);
        var dir = StateDirectory.Open(state.Path);
        using var handling = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var accepted = 0;
        using var server = new IpcServer(dir.Socket, Encoding.UTF8.GetBytes(credential), new BoundPrincipal("op", "team", "agent"),
            new SpikeLimits(), request =>
            {
                handling.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                Interlocked.Increment(ref accepted);
                return new IpcResponse(true, Outcome: "accepted");
            }, _ => { });
        using var daemonLifetime = new CancellationTokenSource();
        using var listener = server.Bind();
        var serving = server.ServeAsync(listener, daemonLifetime.Token);
        using var caller = new CancellationTokenSource();

        var call = new IpcClient(dir, new SpikeLimits(), TimeSpan.FromSeconds(30)).SendAsync(Submit, caller.Token);
        await Bounded.Until(() => handling.IsSet, "request reached handler");
        await caller.CancelAsync();
        var response = await call.WaitAsync(Slack, TestContext.Current.CancellationToken);

        Assert.Equal(IpcProtocol.OutcomeUnknown, response.Error);
        release.Set();
        await Bounded.Until(() => Volatile.Read(ref accepted) == 1, "daemon-side acceptance completed");

        await daemonLifetime.CancelAsync();
        listener.Dispose();
        await serving.WaitAsync(Slack, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Handler_io_exception_is_a_daemon_error_not_a_dropped_client()
    {
        using var state = new TempStateDir();
        var credential = FakePeer.WriteCredential(state);
        var dir = StateDirectory.Open(state.Path);
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var server = new IpcServer(dir.Socket, Encoding.UTF8.GetBytes(credential), new BoundPrincipal("op", "team", "agent"),
            new SpikeLimits(), _ => throw new IOException("Invalid job log header"), messages.Enqueue);
        using var daemonLifetime = new CancellationTokenSource();
        using var listener = server.Bind();
        var serving = server.ServeAsync(listener, daemonLifetime.Token);

        var response = await new IpcClient(dir, new SpikeLimits(), TimeSpan.FromSeconds(5))
            .SendAsync(Submit, TestContext.Current.CancellationToken);

        Assert.Equal(IpcProtocol.InternalError, response.Error);
        Assert.Contains(messages, message => message.Contains("Invalid job log header", StringComparison.Ordinal));
        await daemonLifetime.CancelAsync();
        listener.Dispose();
        await serving.WaitAsync(Slack, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Request_hook_io_exception_returns_error_and_logs_cause()
    {
        using var state = new TempStateDir();
        var credential = FakePeer.WriteCredential(state);
        var dir = StateDirectory.Open(state.Path);
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var server = new IpcServer(dir.Socket, Encoding.UTF8.GetBytes(credential), new BoundPrincipal("op", "team", "agent"),
            new SpikeLimits(), _ => new IpcResponse(true), messages.Enqueue,
            beforeRequest: _ => throw new IOException("legacy row could not be read"));
        using var daemonLifetime = new CancellationTokenSource();
        using var listener = server.Bind();
        var serving = server.ServeAsync(listener, daemonLifetime.Token);

        var response = await new IpcClient(dir, new SpikeLimits(), TimeSpan.FromSeconds(5))
            .SendAsync(Submit, TestContext.Current.CancellationToken);

        Assert.Equal(IpcProtocol.InternalError, response.Error);
        Assert.Contains(messages, message => message.Contains("legacy row could not be read", StringComparison.Ordinal));
        await daemonLifetime.CancelAsync();
        listener.Dispose();
        await serving.WaitAsync(Slack, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Missing_endpoint_is_provably_unsent_without_waiting()
    {
        using var state = new TempStateDir();
        FakePeer.WriteCredential(state);

        var (response, elapsed) = await Timed(() => new IpcClient(StateDirectory.Open(state.Path), new SpikeLimits(), Budget)
            .SendAsync(Submit, CancellationToken.None));

        Assert.Equal(IpcProtocol.DaemonUnavailable, response.Error);
        Assert.True(elapsed < Budget, $"took {elapsed}");
    }

    [Fact]
    public async Task Denied_unix_socket_reports_access_denied_without_lazy_start()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var temp = new TempStateDir();
        Assert.Equal(0, InitCommand.Run(temp.Path, true, null, null));
        var state = StateDirectory.Open(temp.Path);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(state.Socket));
        listener.Listen(1);
        File.SetUnixFileMode(state.Socket, UnixFileMode.None);

        var response = await new IpcClient(state, new SpikeLimits()).SendAsync(Submit, TestContext.Current.CancellationToken);
        Assert.Equal(IpcProtocol.AccessDenied, response.Error);
        Assert.False(string.IsNullOrWhiteSpace(response.ErrorDetail));
        Assert.Equal(1, await ClientCommand.RunAsync(state, "list", new Dictionary<string, string>()));
        Assert.False(File.Exists(Path.Combine(state.Path, "start.lock")));
    }

    [Fact]
    public async Task Denied_start_gate_returns_error_instead_of_throwing()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var temp = new TempStateDir();
        Assert.Equal(0, InitCommand.Run(temp.Path, true, null, null));
        var gate = Path.Combine(temp.Path, "start.lock");
        File.WriteAllText(gate, "");
        File.SetUnixFileMode(gate, UnixFileMode.None);

        Assert.Equal(1, await SetupCommand.StartAsync(new Dictionary<string, string> { ["state-dir"] = temp.Path },
            "/missing/atf", quiet: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_budget_is_rejected(int milliseconds)
    {
        using var state = new TempStateDir();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new IpcClient(StateDirectory.Open(state.Path), new SpikeLimits(), TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void Infinite_budget_is_rejected()
    {
        using var state = new TempStateDir();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new IpcClient(StateDirectory.Open(state.Path), new SpikeLimits(), Timeout.InfiniteTimeSpan));
    }

    static async Task ReadRequestAsync(NetworkStream stream, CancellationToken ct) =>
        await Frames.ReadAsync(stream, IpcJson.Default.IpcRequest, 64 << 20, Timeout.InfiniteTimeSpan, ct);

    static async Task<(IpcResponse, TimeSpan)> Timed(Func<Task<IpcResponse>> send)
    {
        var clock = Stopwatch.StartNew();
        var call = send();
        // Test supervisor bound: a hung client fails the test instead of the run.
        var response = await call.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
        return (response, clock.Elapsed);
    }

    sealed class ManualTimeProvider : TimeProvider
    {
        readonly Lock _sync = new();
        readonly List<ManualTimer> _timers = [];
        long _ticks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_sync)
            {
                _timers.Add(timer);
                timer.Change(dueTime, period);
            }
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            ManualTimer[] due;
            lock (_sync)
            {
                _ticks += duration.Ticks;
                due = [.. _timers.Where(timer => timer.DueAt <= _ticks && !timer.Disposed)];
                foreach (var timer in due) { timer.DueAt = long.MaxValue; }
            }
            foreach (var timer in due) { timer.Fire(); }
        }

        sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
        {
            public long DueAt { get; set; } = long.MaxValue;
            public bool Disposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._sync)
                {
                    if (Disposed) { return false; }
                    DueAt = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
                    return true;
                }
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                lock (clock._sync) { Disposed = true; }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Scripted in-process UDS peer on a private temp state directory.</summary>
    sealed class FakePeer : IDisposable
    {
        readonly TempStateDir _state = new();
        readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        readonly CancellationTokenSource _stop = new();
        readonly List<Socket> _accepted = [];
        int _connections;

        public FakePeer(Func<NetworkStream, CancellationToken, Task> script) : this(accept: true, script)
        {
        }

        public FakePeer(bool accept, Func<NetworkStream, CancellationToken, Task>? script = null)
        {
            WriteCredential(_state);
            State = StateDirectory.Open(_state.Path);
            _listener.Bind(new UnixDomainSocketEndPoint(State.Socket));
            _listener.Listen(4);
            if (accept)
            {
                _ = Task.Run(() => AcceptLoopAsync(script!));
            }
        }

        public StateDirectory State { get; }

        public int Connections => Volatile.Read(ref _connections);

        public IpcClient Client(TimeSpan budget) => new(State, new SpikeLimits(), budget);

        public static string WriteCredential(TempStateDir state)
        {
            const string credential = "test-credential";
            var path = state.File("operator.key");
            File.WriteAllText(path, credential);
            File.SetUnixFileMode(path, StateDirectory.PrivateFile);
            return credential;
        }

        public static async Task AnswerHelloAsync(NetworkStream stream, CancellationToken ct)
        {
            await Frames.ReadAsync(stream, IpcJson.Default.IpcRequest, 1 << 20, Timeout.InfiniteTimeSpan, ct);
            await Frames.WriteAsync(stream, new IpcResponse(true), IpcJson.Default.IpcResponse, ct);
        }

        async Task AcceptLoopAsync(Func<NetworkStream, CancellationToken, Task> script)
        {
            while (!_stop.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await _listener.AcceptAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                Interlocked.Increment(ref _connections);
                lock (_accepted)
                {
                    _accepted.Add(socket);
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await script(new NetworkStream(socket), _stop.Token);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or FrameException)
                    {
                    }
                });
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Dispose();
            lock (_accepted)
            {
                _accepted.ForEach(s => s.Dispose());
            }

            _stop.Dispose();
            _state.Dispose();
        }
    }
}
