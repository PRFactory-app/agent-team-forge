namespace AgentTeamForge.Business.Features.Agents.Terminals;

internal static class InteractiveStartup
{
    // One bound covers tab startup, editor readiness and uncertain delivery evidence.
    internal static TimeSpan Timeout
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("ATF_INTERACTIVE_STARTUP_TIMEOUT_SECONDS");
            return int.TryParse(value, out var seconds) && seconds is >= 30 and <= 900
                ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromMinutes(3);
        }
    }
}

internal sealed class AgentStartupBlockedException(string reason, string hint) : Exception(hint)
{
    public string Reason { get; } = reason;
}
