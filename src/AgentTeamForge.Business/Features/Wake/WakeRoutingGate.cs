namespace AgentTeamForge.Business.Features.Wake;

/// <summary>Serializes notice submission with routing changes in this daemon.</summary>
public static class WakeRoutingGate
{
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static IDisposable Enter()
    {
        Gate.Wait();
        return new Releaser();
    }

    public static async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        return new Releaser();
    }

    sealed class Releaser : IDisposable
    {
        public void Dispose() => Gate.Release();
    }
}
