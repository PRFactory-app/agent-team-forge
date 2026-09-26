using AgentTeamForge.Host.Features.FakeBackend;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Host.Hosting;

// Spike-only command surface; not an approved production CLI.
if (args.Length == 0)
{
    return Usage();
}

var options = ParseOptions([.. args.Skip(args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? 2 : 1)]);
if (args.Length > 2 && args[0] == "client" && args[1] == "stop" && !args[2].StartsWith("--", StringComparison.Ordinal))
{
    options["job"] = args[2];
}
try
{
    switch (args[0])
    {
        case "fake-backend":
            return FakeBackendCommand.Run();
        case "init" when options.TryGetValue("state-dir", out var initDir):
            return InitCommand.Run(initDir, options.ContainsKey("test-profile"),
                options.TryGetValue("queue-limit", out var q) ? int.Parse(q, System.Globalization.CultureInfo.InvariantCulture) : null,
                options.TryGetValue("max-runtime-seconds", out var m) ? int.Parse(m, System.Globalization.CultureInfo.InvariantCulture) : null,
                options.GetValueOrDefault("backends"));
        case "setup":
            return SetupCommand.Run(options);
        case "start":
            return await SetupCommand.StartAsync(options);
        case "stop":
            return SetupCommand.Stop(options);
        case "prune":
            return await PruneCommand.RunAsync(StateDirectory.Open(SetupCommand.ResolveStateDir(options)), options);
        case "prfactory" when args.Length > 1:
            return PRFactoryConnection.Run(StateDirectory.Open(SetupCommand.ResolveStateDir(options)), args[1], options, args);
        case "daemon" when options.TryGetValue("state-dir", out var daemonDir):
            return await DaemonCommand.RunAsync(StateDirectory.Open(daemonDir), options.GetValueOrDefault("test-crash-at"), options.GetValueOrDefault("test-fail-at"));
        case "mcp" when options.TryGetValue("state-dir", out var mcpDir):
            var mcpState = StateDirectory.Open(mcpDir);
            return await JobsMcpBridge.RunAsync(mcpState, SpikeProfileFile.Load(mcpState).TestProfile);
        case "client" when args.Length > 1 && options.TryGetValue("state-dir", out var clientDir):
            return await ClientCommand.RunAsync(StateDirectory.Open(clientDir), args[1], options,
                args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : null);
        default:
            return Usage();
    }
}
catch (StateDirectoryException ex)
{
    Console.Error.WriteLine($"error: {ex.Code}");
    return 78;
}

static int Usage()
{
    Console.Error.WriteLine("usage: atf setup --mode headless|herdr [--state-dir DIR] [--apply] | start|stop [--state-dir DIR] | prfactory connect|disconnect|status [--state-dir DIR] [--url HTTPS_URL --token TOKEN --repo ID=DIR] | prune [--older-than 30d] [--dry-run] [--state-dir DIR] | <init|daemon|mcp|client|fake-backend> --state-dir DIR [options]");
    return 64;
}

static Dictionary<string, string> ParseOptions(string[] rest)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < rest.Length; i++)
    {
        if (!rest[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var name = rest[i][2..];
        var hasValue = i + 1 < rest.Length && !rest[i + 1].StartsWith("--", StringComparison.Ordinal);
        options[name] = hasValue ? rest[++i] : "true";
    }

    return options;
}
