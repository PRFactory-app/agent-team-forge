namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>Stop only a live idle session retained by this daemon's interactive backend.</summary>
public interface IInteractiveSessionStop
{
    bool StopIdleSession(string sessionId);
    void StopAllIdleSessions();
}
