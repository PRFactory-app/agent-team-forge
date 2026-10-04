using System.Globalization;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.DAL.Features.Sessions;

namespace AgentTeamForge.Business.Features.Wake;

/// <summary>A lead's native owner is live while its Claude relay still polls or its host pid exists.
/// Codex targets carry no pid, so they are never proven live.</summary>
public static class LeadOwnerLiveness
{
    public static bool IsLive(LeadSessionOwner owner, ClaudeWakeMailbox claude) => owner.WakeKind switch
    {
        "claude" => claude.HasRecentRelay(owner.WakeAddress ?? "", owner.WakeSecret ?? "", owner.WakeHome ?? "") || PidAlive(owner.WakeHome),
        "pi" => owner.WakeKey is { } key && key.StartsWith("pi:", StringComparison.Ordinal) && PidAlive(key["pi:".Length..]),
        _ => false,
    };

    static bool PidAlive(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0 && HerdrTerminal.PidMayBeAlive(pid);
}
