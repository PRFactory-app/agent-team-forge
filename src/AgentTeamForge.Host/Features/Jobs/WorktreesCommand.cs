using System.Text.Json;
using AgentTeamForge.Business;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Jobs;

public static class WorktreesCommand
{
    public static async Task<int> RunAsync(StateDirectory state, IReadOnlyDictionary<string, string> options)
    {
        var job = options.GetValueOrDefault("job");
        var force = options.ContainsKey("force");
        if (force && job is null || options.TryGetValue("dry-run", out var dry) && dry != "true" || job is { Length: 0 })
        {
            Console.Error.WriteLine("usage: atf worktrees prune [--job ID] [--dry-run] [--force (requires --job)] [--state-dir DIR]");
            return 64;
        }

        var response = await new IpcClient(state, new SpikeLimits()).SendAsync(new IpcRequest
        {
            Op = IpcProtocol.JobPruneWorktrees,
            JobId = job,
            Force = force,
            DryRun = options.ContainsKey("dry-run"),
        }, CancellationToken.None);
        if (!response.Ok)
        {
            Console.Error.WriteLine($"error: {response.Error}");
            return 1;
        }
        var results = response.Worktrees ?? [];
        foreach (var result in results)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(result, IpcJson.Default.WorktreeCleanupResult));
        }
        var acted = results.Count(r => r.Outcome is "removed" or "would_remove");
        Console.Out.WriteLine($"worktrees: {(options.ContainsKey("dry-run") ? "would remove" : "removed")} {acted}, kept {results.Count - acted}");
        return 0;
    }
}
