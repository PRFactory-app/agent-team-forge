using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.DAL.Features.Wake;

namespace AgentTeamForge.Host.Features.Wake;

/// <summary>Reports whether a native write could have begun; only pre-write failure permits resume.</summary>
internal static class ClaudeDeliveryPost
{
    internal static async Task<(bool WriteStarted, bool Posted)> PostAsync(WakeRegistration target, string prompt,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            // ClaudePipe verifies the named pipe owner SID and server PID. A failed
            // post may have written bytes, so Windows retains the N5 fence.
            return (true, await new ClaudeChannelWake().PostAsync(target, prompt, cancellationToken));
        }
        if (!OperatingSystem.IsLinux() || Path.GetFileName(target.Address) != target.Home + ".sock"
            || Path.GetFileName(Path.GetDirectoryName(target.Address)) != "cc-socks") { return (false, false); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(target.Address), deadline.Token); }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { return (false, false); }
        var wire = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ClaudeAuth("auth", target.Secret), WakeJson.Default.ClaudeAuth) + "\n"
            + JsonSerializer.Serialize(new ClaudeUser("user", new ClaudeMessage("user", prompt)), WakeJson.Default.ClaudeUser) + "\n");
        try
        {
            var sent = 0;
            while (sent < wire.Length)
            {
                var written = await socket.SendAsync(wire.AsMemory(sent), SocketFlags.None, deadline.Token);
                if (written == 0) { return (true, false); }
                sent += written;
            }
            socket.Shutdown(SocketShutdown.Send);
            return (true, true);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return (true, false);
        }
    }
}
