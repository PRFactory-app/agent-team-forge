using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Wake;

/// <summary>Only the recipient host's own MCP child writes its native channel.</summary>
internal static class ClaudeWakeRelay
{
    internal static readonly TimeSpan MinPoll = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan MaxPoll = TimeSpan.FromSeconds(3);

    /// <summary>Idle polling backs off to <see cref="MaxPoll"/>; any delivery or a managed child resets it.</summary>
    internal static TimeSpan NextDelay(TimeSpan current, bool busy) =>
        busy ? MinPoll : TimeSpan.FromTicks(Math.Min(MaxPoll.Ticks, current.Ticks * 2));

    internal static async Task RunAsync(IpcRequest? host, IpcClient client, CancellationToken cancellationToken,
        string? managedJobId = null, Func<string?>? sessionId = null, string? workspace = null)
    {
        if (host?.WakeKind != "claude") { return; }
        var target = new WakeRegistration(host.WakeKey!, 0, "claude", host.WakeAddress!, host.WakeSecret!, host.WakeHome!);
        var poster = new ClaudeChannelWake();
        var delay = MinPoll;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var response = await client.SendAsync(host with { Op = IpcProtocol.ClaudeWakeTake }, cancellationToken);
                var busy = response.ClaudeNotice is not null || managedJobId is not null;
                if (response.ClaudeNotice is { } offer)
                {
                    var currentHost = HostSessionWake.CurrentHost();
                    var owned = HostSessionWake.OwnsClaudeChannel(host, currentHost,
                        Environment.GetEnvironmentVariable("CLAUDE_CODE_MESSAGING_SOCKET"),
                        Environment.GetEnvironmentVariable("CLAUDE_CODE_MESSAGING_TOKEN"));
                    if (!owned) { Console.Error.WriteLine("[atf-bridge] Claude wake channel owner changed"); }
                    var posted = owned && await poster.PostAsync(target, offer.Notice, cancellationToken);
                    await client.SendAsync(host with { Op = IpcProtocol.ClaudeWakeComplete, NoticeId = offer.Id, NoticePosted = posted }, cancellationToken);
                }
                if (managedJobId is not null && sessionId?.Invoke() is { } leadSession && workspace is not null)
                {
                    var currentHost = HostSessionWake.CurrentHost();
                    if (HostSessionWake.OwnsClaudeChannel(host, currentHost,
                        Environment.GetEnvironmentVariable("CLAUDE_CODE_MESSAGING_SOCKET"),
                        Environment.GetEnvironmentVariable("CLAUDE_CODE_MESSAGING_TOKEN")))
                    {
                        var claudeHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
                            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                        var taken = await client.SendAsync(host with
                        {
                            Op = IpcProtocol.ClaudeDeliveryTake,
                            JobId = managedJobId,
                            LeadSessionId = leadSession,
                            Workspace = workspace,
                            Text = claudeHome
                        }, cancellationToken);
                        if (taken.ClaudeDelivery is { } delivery)
                        {
                            busy = true;
                            var prompt = delivery.Instruction + "\n\n[AgentTeamForge correlation id: atf-corr:"
                                + delivery.Correlation + " — internal marker, ignore this line]";
                            var (writeStarted, posted) = await ClaudeDeliveryPost.PostAsync(target, prompt, cancellationToken);
                            await client.SendAsync(host with
                            {
                                Op = IpcProtocol.ClaudeDeliveryComplete,
                                JobId = delivery.JobId,
                                LeadSessionId = leadSession,
                                Workspace = workspace,
                                NativeRunId = delivery.RunId,
                                NativeCorrelation = delivery.Correlation,
                                NativeWriteStarted = writeStarted,
                                NoticePosted = posted
                            }, cancellationToken);
                        }
                    }
                }
                delay = NextDelay(delay, busy);
                await Task.Delay(delay, cancellationToken);
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
