using AgentTeamForge.DAL.Files;
using System.Globalization;
using System.Text;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Host.Features.Setup;

/// <summary>In-tab helper: records the shell identity immediately before it execs the agent.</summary>
public static class TerminalTokenCommand
{
    public static int Run(IReadOnlyDictionary<string, string> options)
    {
        if (!OperatingSystem.IsMacOS() || !options.TryGetValue("pid", out var value)
            || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || pid <= 0 || DarwinProcess.ParentPid(Environment.ProcessId) != pid
            || !options.TryGetValue("sidecar", out var sidecar) || !Path.IsPathFullyQualified(sidecar)
            || DarwinProcess.CreationToken(pid) is not { } token)
        {
            return 1;
        }
        try
        {
            using var file = new FileStream(sidecar, PrivateFiles.Options(FileMode.CreateNew, FileAccess.Write));
            file.Write(Encoding.ASCII.GetBytes($"{pid} {token}"));
            file.Flush(flushToDisk: true);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 1; }
    }
}
