namespace AgentTeamForge.Business.Features.Agents.Terminals;

public sealed record InteractiveRetentionSettings(int MaxRetainedSessions = 16, int IdleCloseMinutes = 5)
{
    public bool IsValid() => MaxRetainedSessions is >= 0 and <= 64 && IdleCloseMinutes is >= -1 and <= 1440;
    public TimeSpan IdleTimeout() => TimeSpan.FromMinutes(IdleCloseMinutes);
}
