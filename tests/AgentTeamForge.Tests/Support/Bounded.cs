namespace AgentTeamForge.Tests.Support;

/// <summary>Deadline-bounded condition waits; every wait fails loudly on timeout.</summary>
public static class Bounded
{
    public static readonly TimeSpan ScenarioDeadline = TimeSpan.FromSeconds(30);

    public static async Task<T> Until<T>(Func<Task<T?>> probe, string what, TimeSpan? deadline = null)
        where T : class
    {
        var until = DateTime.UtcNow + (deadline ?? ScenarioDeadline);
        while (DateTime.UtcNow < until)
        {
            var value = await probe();
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"timed out waiting for {what}");
    }

    public static async Task Until(Func<bool> condition, string what, TimeSpan? deadline = null)
    {
        var until = DateTime.UtcNow + (deadline ?? ScenarioDeadline);
        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"timed out waiting for {what}");
    }
}
