using AgentTeamForge.DAL.Features.Wake;

namespace AgentTeamForge.Business.Features.Wake;

public sealed record ClaudeWakeNotice(string Id, string Notice);

/// <summary>Ephemeral notice offers to the recipient's own MCP bridge. Durable messages
/// remain in SQLite; timeout, bridge death and daemon restart leave them unread for retry.</summary>
public sealed class ClaudeWakeMailbox : IWakePoster
{
    sealed class Offer(WakeRegistration target, string notice)
    {
        public WakeRegistration Target { get; } = target;
        public ClaudeWakeNotice Notice { get; } = new(Guid.NewGuid().ToString("N"), notice);
        public TaskCompletionSource<bool> Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Taken;
    }
    readonly Lock sync = new();
    readonly Dictionary<string, Offer> offers = [];

    public async Task<bool> PostAsync(WakeRegistration target, string notice, CancellationToken cancellationToken)
    {
        var offer = new Offer(target, notice);
        lock (sync) { offers.Add(offer.Notice.Id, offer); }
        try { return await offer.Posted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
        catch (TimeoutException) { return false; }
        finally { lock (sync) { offers.Remove(offer.Notice.Id); } }
    }

    public ClaudeWakeNotice? Take(string? address, string? secret, string? host)
    {
        lock (sync)
        {
            var offer = offers.Values.FirstOrDefault(x => !x.Taken && Matches(x, address, secret, host));
            if (offer is null) { return null; }
            offer.Taken = true;
            return offer.Notice;
        }
    }

    public bool Complete(string? id, string? address, string? secret, string? host, bool posted)
    {
        lock (sync)
        {
            return id is not null && offers.TryGetValue(id, out var offer) && offer.Taken
                && Matches(offer, address, secret, host) && offer.Posted.TrySetResult(posted);
        }
    }

    static bool Matches(Offer offer, string? address, string? secret, string? host) =>
        offer.Target.Kind == "claude" && !string.IsNullOrEmpty(secret)
        && offer.Target.Address == address && offer.Target.Secret == secret && offer.Target.Home == host;
}
