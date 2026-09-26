namespace AgentTeamForge.Business.Features.Agents.Backends;

internal static class PiThinking
{
    // Keep in sync with the reference pi backend's _THINKING_OPTIONS.
    internal static bool Valid(string value) => value is "off" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max";
}
