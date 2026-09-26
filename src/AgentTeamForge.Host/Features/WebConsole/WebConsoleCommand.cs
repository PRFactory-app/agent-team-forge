using System.Globalization;
using System.Runtime.InteropServices;
using AgentTeamForge.Business;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.WebConsole;

/// <summary>
/// <c>atf web --state-dir DIR --port PORT</c>: a separate client process serving the
/// operator console on 127.0.0.1. It prints the URL and a fresh per-run bearer to this
/// (authorized) startup console only; the daemon credential never leaves the process.
/// </summary>
public static class WebConsoleCommand
{
    static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(15);

    public static async Task<int> RunAsync(StateDirectory state, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("port", out var portText)
            || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port > 65535)
        {
            Console.Error.WriteLine("usage: atf web --state-dir DIR --port PORT   (binds 127.0.0.1 only; 0 = ephemeral)");
            return 64;
        }

        var client = new IpcClient(state, new SpikeLimits(), CallBudget);
        var token = WebConsoleServer.NewToken();
        await using var server = await WebConsoleServer.StartAsync(port, token, client.SendAsync);
        Console.Out.WriteLine($"url {server.Url}");
        Console.Out.WriteLine($"token {token}");
        Console.Out.Flush();

        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.TrySetResult(); });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; stop.TrySetResult(); });
        await stop.Task;
        await server.StopAsync();
        return 0;
    }
}
