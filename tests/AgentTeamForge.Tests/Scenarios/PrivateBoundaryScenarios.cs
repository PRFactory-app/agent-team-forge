using System.Buffers.Binary;
using System.Net.Sockets;
using AgentTeamForge.Business;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Scenarios;

[Trait("Category", "Scenario")]
public sealed class PrivateBoundaryScenarios
{
    [Fact]
    public async Task Second_daemon_is_refused_without_disturbing_the_live_endpoint()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        await rig.StartDaemonAsync();
        var accepted = await rig.SubmitAsync("k", "x");

        var (exit, _, _) = await rig.RunToExitAsync(["daemon", "--state-dir", rig.StateDir]);

        Assert.Equal(75, exit);
        Assert.True(File.Exists(rig.SocketPath));
        Assert.True((await rig.GetAsync(accepted.Job!.JobId)).Ok);
    }

    [Fact]
    public async Task Bad_credential_identity_version_and_sizes_are_rejected_before_writes()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();
        await rig.StartDaemonAsync();

        Assert.Equal(IpcProtocol.Unauthenticated, (await Hello(rig, new() { ProtocolVersion = 1, Op = "hello", Credential = "wrong" })).Error);
        Assert.Equal(IpcProtocol.UnsupportedVersion, (await Hello(rig, new() { ProtocolVersion = 2, Op = "hello", Credential = rig.Credential })).Error);
        Assert.Equal(IpcProtocol.IdentityMismatch, (await Hello(rig, new() { ProtocolVersion = 1, Op = "hello", Credential = rig.Credential, Principal = "root" })).Error);

        using (var socket = await Connect(rig))
        await using (var stream = new NetworkStream(socket))
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, 10 * 1024 * 1024);
            await stream.WriteAsync(header, TestContext.Current.CancellationToken);
            var response = await Frames.ReadAsync(stream, IpcJson.Default.IpcResponse, 64 * 1024, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(IpcProtocol.FrameTooLarge, response!.Error);
        }

        var oversized = await rig.SubmitAsync("big", new string('x', new SpikeLimits().MaxInstructionChars + 1));
        Assert.Equal(JobErrors.InstructionTooLong, oversized.Error);

        var logs = string.Join('\n', rig.DaemonLog);
        Assert.DoesNotContain(rig.Credential, logs, StringComparison.Ordinal);
    }

    static async Task<IpcResponse> Hello(SpikeRig rig, IpcRequest hello)
    {
        using var socket = await Connect(rig);
        await using var stream = new NetworkStream(socket);
        await Frames.WriteAsync(stream, hello, IpcJson.Default.IpcRequest, TestContext.Current.CancellationToken);
        return (await Frames.ReadAsync(stream, IpcJson.Default.IpcResponse, 64 * 1024, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))!;
    }

    static async Task<Socket> Connect(SpikeRig rig)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(rig.SocketPath), TestContext.Current.CancellationToken);
        return socket;
    }
}
