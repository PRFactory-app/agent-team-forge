using System.Diagnostics;
using System.Text.Json;
using AgentTeamForge.Host.Transport;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentTeamForge.Tests.Support;

/// <summary>
/// Test supervisor for real-process scenarios. Starts daemon, bridges and CLI
/// clients as sibling children of the test process (the daemon is never in a
/// bridge's kill scope). Runs against ATF_HOST_BINARY when set (e.g. the
/// published Native AOT binary), otherwise the built apphost.
/// </summary>
public sealed class SpikeRig : IDisposable
{
    readonly TempStateDir _dir = new();
    readonly OwnedProcesses _processes = new();
    readonly List<string> _daemonLog = [];

    public static string Binary =>
        Environment.GetEnvironmentVariable("ATF_HOST_BINARY") is { Length: > 0 } published
            ? published
            : Path.Combine(AppContext.BaseDirectory, "atf");

    public string StateDir => Path.Combine(_dir.Path, "state");

    public string BarrierDir => Path.Combine(StateDir, "barriers");

    public IReadOnlyList<string> DaemonLog
    {
        get
        {
            lock (_daemonLog)
            {
                return [.. _daemonLog];
            }
        }
    }

    public async Task InitAsync(int? maxRuntimeSeconds = null)
    {
        var args = new List<string> { "init", "--state-dir", StateDir, "--test-profile" };
        if (maxRuntimeSeconds is { } seconds)
        {
            args.AddRange(["--max-runtime-seconds", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        var (exit, _, _) = await RunToExitAsync(args);
        Assert.Equal(0, exit);
    }

    public async Task<Process> StartDaemonAsync(params string[] extra)
    {
        lock (_daemonLog)
        {
            _daemonLog.Clear();
        }

        var process = _processes.Start(Info(["daemon", "--state-dir", StateDir, .. extra], redirectInput: false));
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (_daemonLog)
                {
                    _daemonLog.Add(e.Data);
                }
            }
        };
        process.BeginErrorReadLine();
        process.StandardOutput.Close();
        await Bounded.Until(() => process.HasExited || DaemonLog.Any(l => l.Contains("ready", StringComparison.Ordinal)), "daemon ready");
        Assert.False(process.HasExited, "daemon exited during startup");
        return process;
    }

    public async Task<(Process Process, McpClient Client)> StartBridgeAsync(string? leadParentId = null)
    {
        var info = Info(["mcp", "--state-dir", StateDir], redirectInput: true);
        if (leadParentId is not null)
        {
            info.Environment["WIN_AGENT_TEAMS_PARENT_ID"] = leadParentId;
        }
        var process = _processes.Start(info);
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        var client = await McpClient.CreateAsync(
            new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream),
            cancellationToken: TestContext.Current.CancellationToken);
        return (process, client);
    }

    public static async Task<IpcResponse> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        return JsonSerializer.Deserialize(text, IpcJson.Default.IpcResponse)!;
    }

    /// <summary>Runs the CLI client (a separate short-lived process using the same IPC path).</summary>
    public async Task<IpcResponse> ClientAsync(params string[] args)
    {
        var (_, stdout, _) = await RunToExitAsync(["client", args[0], "--state-dir", StateDir, .. args[1..]]);
        return JsonSerializer.Deserialize(stdout.Trim(), IpcJson.Default.IpcResponse)!;
    }

    public Task<IpcResponse> SubmitAsync(string key, string instruction, string? behavior = null, bool hold = false)
    {
        var args = new List<string> { "submit", "--key", key, "--instruction", instruction };
        if (behavior is not null)
        {
            args.AddRange(["--behavior", behavior]);
        }

        if (hold)
        {
            args.Add("--hold");
        }

        return ClientAsync([.. args]);
    }

    public Task<IpcResponse> GetAsync(string jobId) => ClientAsync("get", "--job", jobId);

    public async Task<IpcResponse> WaitForStatusAsync(string jobId, params string[] statuses) =>
        await Bounded.Until<IpcResponse>(async () =>
        {
            var response = await GetAsync(jobId);
            return response.Job is { } job && statuses.Contains(job.Status) ? response : null;
        }, $"job {jobId} to reach {string.Join('|', statuses)}");

    public Task WaitForAckAsync(string jobId) =>
        Bounded.Until(() => File.Exists(Path.Combine(BarrierDir, $"acked-{jobId}")), $"fake ack for {jobId}");

    public void Release(string jobId) => File.WriteAllText(Path.Combine(BarrierDir, $"release-{jobId}"), "go");

    public int Invocations(string jobId)
    {
        var log = Path.Combine(BarrierDir, "invocations.log");
        return File.Exists(log) ? File.ReadAllLines(log).Count(l => l.StartsWith(jobId + " ", StringComparison.Ordinal)) : 0;
    }

    public string Credential => File.ReadAllText(Path.Combine(StateDir, "operator.key")).Trim();

    public string SocketPath => Path.Combine(StateDir, "daemon.sock");

    public async Task<(int Exit, string Stdout, string Stderr)> RunToExitAsync(IReadOnlyList<string> args)
    {
        var process = _processes.Start(Info(args, redirectInput: false));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(Bounded.ScenarioDeadline);
        await process.WaitForExitAsync(deadline.Token);
        return (process.ExitCode, await stdout, await stderr);
    }

    static ProcessStartInfo Info(IReadOnlyList<string> args, bool redirectInput)
    {
        var info = new ProcessStartInfo(Binary)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        return info;
    }

    public void Dispose()
    {
        _processes.Dispose();
        // Orphaned fake children (after a daemon kill) are released by barrier, never killed by PID.
        if (Directory.Exists(BarrierDir))
        {
            foreach (var acked in Directory.GetFiles(BarrierDir, "acked-*"))
            {
                var jobId = Path.GetFileName(acked)["acked-".Length..];
                File.WriteAllText(Path.Combine(BarrierDir, $"release-{jobId}"), "cleanup");
            }
        }

        Thread.Sleep(100);
        _dir.Dispose();
    }
}
