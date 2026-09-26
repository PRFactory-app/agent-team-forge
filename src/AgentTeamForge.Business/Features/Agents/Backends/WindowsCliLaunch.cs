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
            EnsureCmdSafe(info.ArgumentList);
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

    /// <summary>
    /// cmd.exe re-parses a .cmd shim's arguments (PowerShell 5.1 leaves unspaced ones
    /// unquoted and expands %VAR% even inside quotes), so a caller-supplied model name
    /// or path with a cmd metacharacter could run commands. Prompts never go here.
    /// </summary>
    internal static void EnsureCmdSafe(IEnumerable<string> args)
    {
        if (args.Any(arg => arg.Any(c => char.IsControl(c) || c is '&' or '|' or '<' or '>' or '^' or '%' or '!')))
        {
            throw new BackendNotStartedException("argument is unsafe for a Windows .cmd shim");
        }
    }

    internal static string PowerShellCommand(string shim, IEnumerable<string> args) =>
        "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false); "
        + "& " + string.Join(' ', new[] { shim }.Concat(args.Select(ShimArgument)).Select(PowerShellText.Quote))
        + "; exit $LASTEXITCODE";

    /// <summary>
    /// Windows PowerShell 5.1 (and pwsh for .cmd/.bat) passes native arguments without escaping
    /// embedded double quotes, so the shim's runtime would strip them from JSON/TOML values.
    /// Pre-escape them by MSVC argv rules; PowerShell only adds the surrounding quotes.
    /// </summary>
    internal static string ShimArgument(string arg)
    {
        if (!arg.Contains('"'))
        {
            return arg;
        }
        var escaped = new System.Text.StringBuilder();
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            escaped.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }
        // PowerShell wraps an argument with whitespace in quotes; trailing backslashes must not escape that quote.
        return escaped.Append('\\', arg.Any(char.IsWhiteSpace) ? backslashes * 2 : backslashes).ToString();
    }
}
