using System.Text.Json;
using AgentTeamForge.Business;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Jobs;

/// <summary>Operator CLI client (same IPC path as the MCP bridge); prints one JSON response.</summary>
public static class ClientCommand
{
    public static async Task<int> RunAsync(StateDirectory state, string verb, IReadOnlyDictionary<string, string> options)
    {
        var request = verb switch
        {
            "submit" => new IpcRequest
            {
                Op = IpcProtocol.JobSubmit,
                IdempotencyKey = options.GetValueOrDefault("key"),
                Instruction = options.GetValueOrDefault("instruction"),
                Behavior = options.GetValueOrDefault("behavior"),
                Hold = options.ContainsKey("hold"),
                Backend = options.GetValueOrDefault("backend"),
                Cwd = options.TryGetValue("cwd", out var cwd) ? Path.GetFullPath(cwd) : null,
            },
            "follow-up" => new IpcRequest
            {
                Op = IpcProtocol.JobFollowUp,
                JobId = options.GetValueOrDefault("job"),
                IdempotencyKey = options.GetValueOrDefault("key"),
                Instruction = options.GetValueOrDefault("instruction"),
            },
            "get" => new IpcRequest { Op = IpcProtocol.JobGet, JobId = options.GetValueOrDefault("job") },
            "stop" => new IpcRequest { Op = IpcProtocol.JobStop, JobId = options.GetValueOrDefault("job") },
            "list" => new IpcRequest
            {
                Op = IpcProtocol.JobList,
                Status = options.GetValueOrDefault("status"),
                // An unparsable limit is sent as 0 so the daemon rejects it rather than defaulting.
                Limit = options.TryGetValue("limit", out var limit) ? (int.TryParse(limit, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0) : null,
                Cursor = options.GetValueOrDefault("cursor"),
            },
            _ => null,
        };
        if (request is null)
        {
            Console.Error.WriteLine("usage: atf client <submit|follow-up|get|stop|list> --state-dir DIR "
                + "[--key K --instruction TEXT [--backend fake|claude|codex|pi] [--cwd DIR] [--behavior B] [--hold] | --job ID [--key K --instruction TEXT] | stop ID | [--status S] [--limit N] [--cursor C]]");
            return 64;
        }

        var response = await new IpcClient(state, new SpikeLimits()).SendAsync(request, CancellationToken.None);
        Console.Out.WriteLine(JsonSerializer.Serialize(response, IpcJson.Default.IpcResponse));
        return response.Ok ? 0 : 1;
    }
}
