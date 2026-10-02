namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>Stop only a live idle session retained by this daemon's interactive backend.</summary>
/// <summary>What a sustained probe proved about a retained pane; anything short of proof is <see cref="Unverified"/>.</summary>
public enum SessionIdleState { Idle, Busy, Unverified }

public interface IInteractiveSessionStop
{
    bool HasIdleSession(string sessionId);
    bool? HasLiveSession(string sessionId) => null;
    void ReleaseNativeTurn(string sessionId) { }
    /// <summary>Multi-sample idle proof for bulk cleanup. Backends that cannot prove idleness never report <see cref="SessionIdleState.Idle"/>.</summary>
    Task<SessionIdleState> ProbeIdleAsync(string sessionId) => Task.FromResult(SessionIdleState.Unverified);
    bool StopIdleSession(string sessionId);
    void StopAllIdleSessions();
}
