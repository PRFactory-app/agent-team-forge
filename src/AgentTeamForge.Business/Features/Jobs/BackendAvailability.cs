namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Configured backend executables visible to the daemon; does not launch or authenticate an agent.</summary>
public static class BackendAvailability
{
    public static IReadOnlyDictionary<string, bool> Read(IEnumerable<string> configured, string? launchMode = null) =>
        configured.ToDictionary(name => name, name => name == "fake" ||
            (!(name is "cursor" or "droid" && launchMode is not null and not "headless")
                && Find(name == "cursor" ? "cursor-agent" : name)));

    static bool Find(string name)
    {
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';') : [""];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) { continue; }
            foreach (var extension in extensions)
            {
                var path = Path.Combine(directory, name + extension);
                try
                {
                    if (File.Exists(path) && (OperatingSystem.IsWindows()
                        || (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return false;
    }
}
