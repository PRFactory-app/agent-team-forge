using AgentTeamForge.Business.Features.Jobs;

namespace AgentTeamForge.Business.Features.Agents.Backends;

internal static class PiThinking
{
    // The list lives in ModelSelection; keep it in sync with the reference pi backend's _THINKING_OPTIONS.
    internal static bool Valid(string value) => ModelSelection.Efforts("pi").Contains(value, StringComparer.Ordinal);
}
