using System.Text.Json;
using AgentTeamForge.Business;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Jobs;

/// <summary>Operator CLI client (same IPC path as the MCP bridge); prints one JSON response.</summary>
public static class ClientCommand
{
    public static async Task<int> RunAsync(StateDirectory state, string verb, IReadOnlyDictionary<string, string> options, string? positionalJobId = null)
    {
        if (verb == "logs")
        {
            var id = positionalJobId ?? options.GetValueOrDefault("job");
            if (string.IsNullOrWhiteSpace(id))
            {
                Console.Error.WriteLine("usage: atf client logs <id> [--follow] --state-dir DIR");
                return 64;
            }

            if (!await EnsureDaemonAsync(state))
            {
                return 1;
            }

            return await LogsAsync(state, id, options.ContainsKey("follow"));
        }

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
                Worktree = options.ContainsKey("worktree"),
                TimeoutSeconds = Seconds(options, "timeout"),
                QueueTtlSeconds = Seconds(options, "queue-ttl"),
            },
            "follow-up" => new IpcRequest
            {
                Op = IpcProtocol.JobFollowUp,
                JobId = options.GetValueOrDefault("job"),
                IdempotencyKey = options.GetValueOrDefault("key"),
                Instruction = options.GetValueOrDefault("instruction"),
                Interrupt = options.ContainsKey("interrupt"),
                TimeoutSeconds = Seconds(options, "timeout"),
                QueueTtlSeconds = Seconds(options, "queue-ttl"),
            },
            "get" => new IpcRequest { Op = IpcProtocol.JobGet, JobId = options.GetValueOrDefault("job") },
            "stop" => new IpcRequest { Op = IpcProtocol.JobStop, JobId = options.GetValueOrDefault("job") },
            "list" => new IpcRequest
            {
                Op = IpcProtocol.JobList,
                Status = options.GetValueOrDefault("status"),
                Backend = options.GetValueOrDefault("backend"),
                Since = options.GetValueOrDefault("since"),
                // An unparsable limit is sent as 0 so the daemon rejects it rather than defaulting.
                Limit = options.TryGetValue("limit", out var limit) ? (int.TryParse(limit, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0) : null,
                Cursor = options.GetValueOrDefault("cursor"),
            },
            _ => null,
        };
        if (request is null)
        {
            Console.Error.WriteLine("usage: atf client <submit|follow-up|get|stop|list|logs> --state-dir DIR "
                + "[--key K --instruction TEXT [--backend fake|claude|codex|pi] [--cwd DIR] [--worktree] [--timeout S] [--queue-ttl S] [--behavior B] [--hold] | --job ID [--key K --instruction TEXT [--interrupt] [--timeout S] [--queue-ttl S]] | stop ID | [--status S] [--backend B] [--since ISO-TIME] [--limit N] [--cursor C]]");
            return 64;
        }

        if (!await EnsureDaemonAsync(state))
        {
            return 1;
        }

        var response = await new IpcClient(state, new SpikeLimits()).SendAsync(request, CancellationToken.None);
        Console.Out.WriteLine(JsonSerializer.Serialize(response, IpcJson.Default.IpcResponse));
        return response.Ok ? 0 : 1;
    }

    static async Task<bool> EnsureDaemonAsync(StateDirectory state)
    {
        _ = StateDirectory.ReadPrivateFile(state.CredentialFile);
        return await SetupCommand.StartAsync(new Dictionary<string, string> { ["state-dir"] = state.Path }, quiet: true) == 0;
    }

    /// <summary>An unparsable value is sent as 0 so the daemon rejects it rather than ignoring it.</summary>
    static int? Seconds(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value)
            ? int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : 0
            : null;

    static async Task<int> LogsAsync(StateDirectory state, string jobId, bool follow)
    {
        var client = new IpcClient(state, new SpikeLimits());
        var stdout = Console.OpenStandardOutput();
        long offset = 0;
        var terminalPolls = 0;
        while (true)
        {
            var response = await client.SendAsync(new IpcRequest { Op = IpcProtocol.JobOutput, JobId = jobId, Offset = offset }, CancellationToken.None);
            if (!response.Ok || response.Output is null)
            {
                Console.Error.WriteLine(response.Error ?? "missing_output");
                return 1;
            }

            var output = response.Output;
            if (output.Truncated)
            {
                Console.Error.WriteLine($"log truncated; resumed at byte {output.StartOffset}");
            }

            var bytes = Convert.FromBase64String(output.DataBase64);
            await stdout.WriteAsync(bytes);
            await stdout.FlushAsync();
            offset = output.NextOffset;
            if (offset < output.EndOffset)
            {
                continue;
            }

            if (!follow)
            {
                return 0;
            }

            var job = await client.SendAsync(new IpcRequest { Op = IpcProtocol.JobGet, JobId = jobId }, CancellationToken.None);
            if (!job.Ok)
            {
                Console.Error.WriteLine(job.Error ?? "job_unavailable");
                return 1;
            }

            terminalPolls = job.Job?.Status is "completed" or "failed" or "needs_reconciliation" or "cancelled" ? terminalPolls + 1 : 0;
            if (terminalPolls >= 2)
            {
                return 0;
            }

            await Task.Delay(500);
        }
    }
}
