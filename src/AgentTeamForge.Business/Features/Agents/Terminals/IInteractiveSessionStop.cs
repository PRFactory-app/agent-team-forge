namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>Stop only a live idle session retained by this daemon's interactive backend.</summary>
public interface IInteractiveSessionStop
{
    bool HasIdleSession(string sessionId);
    bool? HasLiveSession(string sessionId) => null;
    void ReleaseNativeTurn(string sessionId) { }
    bool StopIdleSession(string sessionId);
    void StopAllIdleSessions();
}
