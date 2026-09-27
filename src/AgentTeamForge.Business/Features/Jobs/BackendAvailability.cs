using System.Diagnostics;
using AgentTeamForge.Business.Features.Processes;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Local CLI availability and a cached, read-only Cursor sign-in probe.</summary>
public static class BackendAvailability
{
    static readonly Lock StatusGate = new();
    static (DateTimeOffset Until, string Value) _cursorStatus;

    public static IReadOnlyDictionary<string, bool> ReadInstalled(IEnumerable<string> configured) =>
        configured.ToDictionary(name => name, name => name == "fake" || Find(name == "cursor" ? "cursor-agent" : name));

    public static IReadOnlyDictionary<string, bool> Read(IEnumerable<string> configured, string? launchMode = null) =>
        ReadInstalled(configured).ToDictionary(entry => entry.Key, entry => entry.Value
            && !(entry.Key is "cursor" or "droid" && launchMode is not null and not "headless"));

    public static IReadOnlyDictionary<string, string> ReadSignIn(IReadOnlyDictionary<string, bool> availability) =>
        availability.ToDictionary(entry => entry.Key, entry => !entry.Value ? "not_installed"
            : entry.Key == "fake" ? "not_applicable"
            : entry.Key == "cursor" ? CursorStatus()
            : "not_checked");

    static string CursorStatus()
    {
        lock (StatusGate)
        {
            if (_cursorStatus.Until > DateTimeOffset.UtcNow) { return _cursorStatus.Value; }
            var status = ProbeCursorStatus();
            _cursorStatus = (DateTimeOffset.UtcNow.AddSeconds(30), status);
            return status;
        }
    }

    static string ProbeCursorStatus()
    {
        try
        {
            var info = new ProcessStartInfo("cursor-agent")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            info.ArgumentList.Add("status");
            using var process = NonInteractiveProcess.Start(info);
            if (process is null) { return "unknown"; }
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                return "unknown";
            }
            if (!Task.WhenAll(output, error).Wait(3000)) { return "unknown"; }
            var result = (output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult()).ToLowerInvariant();
            if (result.Contains("not logged in", StringComparison.Ordinal)
                || result.Contains("logged out", StringComparison.Ordinal)) { return "signed_out"; }
            return process.ExitCode == 0 && result.Contains("logged in", StringComparison.Ordinal)
                ? "signed_in" : "unknown";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or AggregateException)
        {
            return "unknown";
        }
    }

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
