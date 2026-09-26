using System.Text.Json;
using AgentTeamForge.Business;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Jobs;

public static class PruneCommand
{
    public static async Task<int> RunAsync(StateDirectory state, IReadOnlyDictionary<string, string> options)
    {
        var age = options.GetValueOrDefault("older-than", "30d");
        if (age.Length < 2 || age[^1] != 'd'
            || !int.TryParse(age[..^1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var days)
            || days is < 1 or > 36500 || options.TryGetValue("dry-run", out var dryRun) && dryRun != "true")
        {
            Console.Error.WriteLine("usage: atf prune [--older-than 30d] [--dry-run] [--state-dir DIR]");
            return 64;
        }

        var response = await new IpcClient(state, new SpikeLimits()).SendAsync(new IpcRequest
        {
            Op = IpcProtocol.JobPrune,
            OlderThanDays = days,
            DryRun = options.ContainsKey("dry-run"),
        }, CancellationToken.None);
        Console.Out.WriteLine(JsonSerializer.Serialize(response, IpcJson.Default.IpcResponse));
        return response.Ok ? 0 : 1;
    }
}
