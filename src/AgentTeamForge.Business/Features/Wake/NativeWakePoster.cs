using AgentTeamForge.DAL.Features.Wake;

namespace AgentTeamForge.Business.Features.Wake;

public sealed class NativeWakePoster(string stateDirectory, ClaudeWakeMailbox claude) : IWakePoster
{
    readonly CodexQueueWake codex = new();
    readonly PiExtensionWake pi = new(stateDirectory);
    public Task<bool> PostAsync(WakeRegistration target, string notice, CancellationToken cancellationToken) => target.Kind switch
    {
        "claude" => claude.PostAsync(target, notice, cancellationToken),
        "codex" => codex.PostAsync(target, notice, cancellationToken),
        "pi" => pi.PostAsync(target, notice, cancellationToken),
        _ => Task.FromResult(false),
    };

    public async Task<string> PostWithReasonAsync(WakeRegistration target, string notice, CancellationToken cancellationToken) =>
        target.Kind == "claude" ? await claude.PostWithReasonAsync(target, notice, cancellationToken)
            : await PostAsync(target, notice, cancellationToken) ? WakePost.Ok : WakePost.Rejected;
}
