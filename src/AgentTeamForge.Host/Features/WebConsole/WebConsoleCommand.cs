using System.ComponentModel;
using System.Diagnostics;
using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Host.Hosting;

namespace AgentTeamForge.Host.Features.WebConsole;

/// <summary>Print the daemon-hosted console link; rotate its bearer only on request.</summary>
public static class WebConsoleCommand
{
    public static int Run(IReadOnlyDictionary<string, string> options)
    {
        if (options.ContainsKey("port"))
        {
            Console.Error.WriteLine("error: configure the console port with atf setup --web-port PORT");
            return 64;
        }

        var state = StateDirectory.Open(SetupCommand.ResolveStateDir(options));
        var port = SetupCommand.ConfiguredWebPort(state);
        var token = options.ContainsKey("rotate-token") ? WebConsoleToken.Rotate(state) : WebConsoleToken.Ensure(state);
        var url = $"http://127.0.0.1:{port}/#token={token}";
        Console.Out.WriteLine($"url {url}");
        if (!options.ContainsKey("open"))
        {
            return 0;
        }

        try
        {
            var info = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(url) { UseShellExecute = true }
                : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
            if (!OperatingSystem.IsWindows())
            {
                info.ArgumentList.Add(url);
            }
            using var process = Process.Start(info) ?? throw new Win32Exception("browser opener unavailable");
            return 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"error: could not open browser: {ex.Message}");
            return 1;
        }
    }
}
