using System.Diagnostics;
using AgentTeamForge.Business.Features.Agents.Terminals;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>Resolve npm-installed Windows CLIs, including their .cmd shim fallback.</summary>
internal static class WindowsCliLaunch
{
    internal static void Configure(ProcessStartInfo info, string kind, bool useDefault)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var launch = useDefault ? kind == "pi" ? WtTabControl.WindowsPiLauncher()
            : [WtTabControl.WindowsAgentBinary(kind)] : [info.FileName];
        if (launch.Count > 1)
        {
            info.FileName = launch[0];
            info.ArgumentList.Insert(0, launch[1]);
        }
        else if (launch[0].EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var command = PowerShellCommand(launch[0], info.ArgumentList);
            info.FileName = "powershell.exe";
            info.ArgumentList.Clear();
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add(command);
        }
        else
        {
            info.FileName = launch[0];
        }
    }

    internal static string PowerShellCommand(string shim, IEnumerable<string> args) =>
        "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false); "
        + "& " + string.Join(' ', new[] { shim }.Concat(args).Select(PowerShellText.Quote))
        + "; exit $LASTEXITCODE";
}
