using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Wake;

/// <summary>Only the recipient host's own MCP child writes its native channel.</summary>
internal static class ClaudeWakeRelay
{
    internal static async Task RunAsync(IpcRequest? host, IpcClient client, CancellationToken cancellationToken)
    {
        if (host?.WakeKind != "claude") { return; }
        var target = new WakeRegistration(host.WakeKey!, 0, "claude", host.WakeAddress!, host.WakeSecret!, host.WakeHome!);
        var poster = new ClaudeChannelWake();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var response = await client.SendAsync(host with { Op = IpcProtocol.ClaudeWakeTake }, cancellationToken);
                if (response.ClaudeNotice is { } offer)
                {
                    var currentHost = OperatingSystem.IsLinux() ? HostSessionWake.NearestHost()
                        : OperatingSystem.IsWindows() ? WindowsHostAncestry.NearestHost() : MacHostAncestry.NearestHost();
                    var owned = HostSessionWake.OwnsClaudeChannel(host, currentHost,
                        Environment.GetEnvironmentVariable("CLAUDE_CODE_MESSAGING_SOCKET"),
                        Environment.GetEnvironmentVariable("CLAUDE_CODE_MESSAGING_TOKEN"));
                    if (!owned) { Console.Error.WriteLine("[atf-bridge] Claude wake channel owner changed"); }
                    var posted = owned && await poster.PostAsync(target, offer.Notice, cancellationToken);
                    await client.SendAsync(host with { Op = IpcProtocol.ClaudeWakeComplete, NoticeId = offer.Id, NoticePosted = posted }, cancellationToken);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Console.Error.WriteLine($"[atf-bridge] Claude wake relay failed: {ex.GetType().Name}");
                // No endpoint/token details in stderr. A failed relay never marks the durable inbox read.
                try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
