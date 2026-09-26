using System.Runtime.CompilerServices;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Tests.Support;

/// <summary>
/// In-process backend for Business dispatch rules. Process-level behaviour is
/// covered separately by the real fake-backend child in Scenarios.
/// </summary>
public sealed class ScriptedBackend(Func<BackendRequest, IEnumerable<BackendEvidence>> script) : IJobBackend
{
    public Action<BackendRequest>? OnStart { get; init; }

    public bool NeverStarts { get; init; }

    public bool Hangs { get; init; }

    public List<BackendRequest> Started { get; } = [];

    public int Terminations { get; private set; }

    public IBackendRun Start(BackendRequest request)
    {
        if (NeverStarts)
        {
            throw new BackendNotStartedException("scripted");
        }

        OnStart?.Invoke(request);
        Started.Add(request);
        return new Run(this, script(request));
    }

    sealed class Run(ScriptedBackend owner, IEnumerable<BackendEvidence> evidence) : IBackendRun
    {
        public int? ProcessId => 4242;

        public Task DeliverAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async IAsyncEnumerable<BackendEvidence> ReadEvidenceAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var item in evidence)
            {
                yield return item;
            }

            if (owner.Hangs)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }

        public void TerminateOwnedChild() => owner.Terminations++;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
