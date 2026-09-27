using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentTeamForge.DAL.Features.Wake;

namespace AgentTeamForge.Business.Features.Wake;

/// <summary>Port of native_wake.post_claude_notice: auth line, user line, bounded Unix socket / Windows pipe send.</summary>
public sealed class ClaudeChannelWake : IWakePoster
{
    public async Task<bool> PostAsync(WakeRegistration target, string notice, CancellationToken cancellationToken)
    {
        if (ClaudeChannel.Transport(ClaudeChannel.Platform) is null || target.Kind != "claude")
        {
            return false;
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var auth = JsonSerializer.Serialize(new ClaudeAuth("auth", target.Secret), WakeJson.Default.ClaudeAuth) + "\n";
            var message = JsonSerializer.Serialize(new ClaudeUser("user", new ClaudeMessage("user", notice)), WakeJson.Default.ClaudeUser) + "\n";
            var wire = Encoding.UTF8.GetBytes(auth + message);
            if (OperatingSystem.IsWindows())
            {
                return ClaudeChannel.Valid(target.Address, target.Secret, target.Home)
                    && await ClaudePipe.PostAsync(target.Address, int.Parse(target.Home, System.Globalization.CultureInfo.InvariantCulture), wire, deadline.Token);
            }
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(target.Address), deadline.Token);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await stream.WriteAsync(wire, deadline.Token);
            await stream.FlushAsync(deadline.Token);
            socket.Shutdown(SocketShutdown.Send);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }
}
