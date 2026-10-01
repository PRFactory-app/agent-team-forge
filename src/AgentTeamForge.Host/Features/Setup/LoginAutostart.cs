using System.Security;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using AgentTeamForge.Host.Hosting;

namespace AgentTeamForge.Host.Features.Setup;

/// <summary>Optional user login registration. Paths are injectable for tests.</summary>
public static partial class LoginAutostart
{
    const string Name = "agentteamforge";
    const string MacLabel = "com.agentteamforge.daemon";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsInstalled(string home, string? platform = null)
    {
        platform ??= Platform();
        if (platform == "windows")
        {
            return OperatingSystem.IsWindows() && WindowsInstalled();
        }
        return File.Exists(FilePath(home, platform));
    }

    public static int Apply(string home, string binary, string stateDir, bool enable,
        Func<string, IReadOnlyList<string>, (int ExitCode, string Output)> commandRunner, string? platform = null)
    {
        platform ??= Platform();
        if (platform == "windows")
        {
            return OperatingSystem.IsWindows() ? ApplyWindows(binary, stateDir, enable) : throw new PlatformNotSupportedException();
        }

        var path = FilePath(home, platform);
        var previous = File.Exists(path) ? File.ReadAllText(path) : null;
        if (!enable && platform == "linux" && File.Exists(path))
        {
            var (code, output) = commandRunner("systemctl", ["--user", "disable", Name + ".service"]);
            if (code != 0)
            {
                Console.Error.WriteLine($"error: systemd user autostart disable failed: {output}");
                return 1;
            }
        }
        string? launchdDomain = null;
        if (platform == "macos")
        {
            launchdDomain = LaunchdDomain(commandRunner);
            if (launchdDomain is null)
            {
                Console.Error.WriteLine("error: launchd autostart failed: cannot determine the user id");
                return 1;
            }
        }
        // A loaded job outlives its plist; unload it so "off" also holds for the current login session.
        // The label is per uid, so a job another HOME or state directory loaded is left running.
        if (!enable && launchdDomain is not null && LoadedJob(launchdDomain, commandRunner) is { } loadedJob)
        {
            if (!OwnsLoadedJob(loadedJob, binary, stateDir, previous))
            {
                Console.Error.WriteLine($"note: left {launchdDomain}/{MacLabel} loaded: {ForeignJob(loadedJob)}");
            }
            else if (commandRunner("launchctl", ["bootout", launchdDomain + "/" + MacLabel]) is (not 0, var output))
            {
                Console.Error.WriteLine($"error: launchd autostart disable failed: {output}".Trim());
                return 1;
            }
        }
        if (enable)
        {
            StateDirectory.CreatePrivateDirectory(Path.GetDirectoryName(path)!);
            // Login managers start with a minimal PATH; keep the setup shell's so agents and herdr resolve.
            var searchPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            var content = platform == "linux" ? LinuxUnit(binary, stateDir, searchPath) : MacPlist(binary, stateDir, searchPath);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, content);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
        else
        {
            Console.Out.WriteLine("Login autostart: off");
            return 0;
        }

        if (enable && launchdDomain is not null && LoadLaunchdJob(launchdDomain, path, binary, stateDir, previous, commandRunner) is ({ } loadError, var unloaded))
        {
            if (previous is null)
            {
                File.Delete(path);
            }
            else
            {
                File.WriteAllText(path, previous);
                if (unloaded)
                {
                    commandRunner("launchctl", ["bootstrap", launchdDomain, path]);
                }
            }
            Console.Error.WriteLine($"error: launchd autostart enable failed: {loadError}".Trim());
            return 1;
        }

        if (platform == "linux")
        {
            var (reloadCode, reloadOutput) = commandRunner("systemctl", ["--user", "daemon-reload"]);
            var (enableCode, enableOutput) = enable ? commandRunner("systemctl", ["--user", "enable", Name + ".service"]) : (0, "");
            if (reloadCode != 0 || enableCode != 0)
            {
                if (enable)
                {
                    if (previous is null)
                    {
                        File.Delete(path);
                    }
                    else
                    {
                        File.WriteAllText(path, previous);
                    }
                    commandRunner("systemctl", ["--user", "daemon-reload"]);
                }
                Console.Error.WriteLine($"error: systemd user autostart {(enable ? "enable" : "disable")} failed: {reloadOutput} {enableOutput}".Trim());
                return 1;
            }
        }
        Console.Out.WriteLine($"Login autostart: {(enable ? "on" : "off")}");
        return 0;
    }

    internal static bool RemoveOwned(string home, string binary, string stateDir,
        Func<string, IReadOnlyList<string>, (int ExitCode, string Output)> commandRunner)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }
        var platform = OperatingSystem.IsMacOS() ? "macos" : "linux";
        var path = FilePath(home, platform);
        if (!File.Exists(path))
        {
            return true;
        }
        var owned = RunsDaemon(File.ReadAllText(path), platform, binary, stateDir);
        return !owned || Apply(home, binary, stateDir, enable: false, commandRunner, platform) == 0;
    }

    internal static string FilePath(string home, string platform) => platform switch
    {
        "linux" => Path.Combine(home, ".config", "systemd", "user", Name + ".service"),
        "macos" => Path.Combine(home, "Library", "LaunchAgents", MacLabel + ".plist"),
        _ => throw new PlatformNotSupportedException(platform),
    };

    static string? LaunchdDomain(Func<string, IReadOnlyList<string>, (int ExitCode, string Output)> commandRunner)
    {
        var (code, output) = commandRunner("id", ["-u"]);
        return code == 0 && uint.TryParse(output.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var uid) ? $"gui/{uid}" : null;
    }

    /// <summary>`launchctl print` of the loaded job, or null when no job carries the label.</summary>
    static string? LoadedJob(string domain, Func<string, IReadOnlyList<string>, (int ExitCode, string Output)> commandRunner) =>
        commandRunner("launchctl", ["print", domain + "/" + MacLabel]) is (0, var output) ? output : null;

    // The loaded job is this installation's when it runs this binary on this state directory, or what
    // this HOME's plist registered before (a re-setup that changes either). Anything else, including a
    // job whose arguments cannot be read, belongs to another HOME or state directory under the same uid.
    static bool OwnsLoadedJob(string loadedJob, string binary, string stateDir, string? previous)
    {
        if (LoadedArguments(loadedJob) is not [var program, "daemon", "--state-dir", var loadedState])
        {
            return false;
        }
        var loadedProgram = loadedJob.Split('\n').Select(line => line.Trim('\t', '\r'))
            .FirstOrDefault(line => line.StartsWith("program = ", StringComparison.Ordinal))?["program = ".Length..] ?? program;
        bool Runs(string ownBinary, string ownState) => CanonicalPath.Same(program, ownBinary)
            && CanonicalPath.Same(loadedProgram, ownBinary) && CanonicalPath.Same(loadedState, ownState);
        return Runs(binary, stateDir) || previous is not null && PlistArgumentsPattern().Match(previous) is { Success: true } plist
            && Runs(XmlUnescape(plist.Groups[1].Value), XmlUnescape(plist.Groups[2].Value));
    }

    // `launchctl print` lists the job's argv one per line inside "arguments = { ... }".
    static List<string>? LoadedArguments(string loadedJob)
    {
        List<string>? arguments = null;
        foreach (var line in loadedJob.Split('\n').Select(line => line.TrimEnd('\r')))
        {
            if (arguments is null)
            {
                arguments = line.Trim() == "arguments = {" ? [] : null;
            }
            else if (line.Trim() == "}")
            {
                return arguments;
            }
            else
            {
                arguments.Add(line.TrimStart('\t'));
            }
        }
        return null;
    }

    static string ForeignJob(string loadedJob) =>
        $"it runs {(LoadedArguments(loadedJob) is { } arguments ? string.Join(' ', arguments) : "an unreadable command")}, not this binary and state directory";

    // Writing the plist only takes effect at the next login; bootstrap loads it now, and RunAtLoad
    // starts the daemon under launchd. A job already loaded from identical content is left running.
    static (string? Error, bool Unloaded) LoadLaunchdJob(string domain, string path, string binary, string stateDir, string? previous,
        Func<string, IReadOnlyList<string>, (int ExitCode, string Output)> commandRunner)
    {
        var unloaded = false;
        if (LoadedJob(domain, commandRunner) is { } loadedJob)
        {
            if (!OwnsLoadedJob(loadedJob, binary, stateDir, previous))
            {
                return ($"{domain}/{MacLabel} is loaded and {ForeignJob(loadedJob)}; "
                    + "run setup --autostart=off with the HOME that installed it first", false);
            }
            if (previous == File.ReadAllText(path))
            {
                return (null, false);
            }
            var (unloadCode, unloadOutput) = commandRunner("launchctl", ["bootout", domain + "/" + MacLabel]);
            if (unloadCode != 0)
            {
                return (unloadOutput, false);
            }
            unloaded = true;
        }
        var (code, output) = commandRunner("launchctl", ["bootstrap", domain, path]);
        return (code == 0 ? null : output, unloaded);
    }

    // Lazy start goes through the unit only when it runs this binary on this
    // state directory; a stale or foreign unit would start the wrong daemon.
    internal static bool UseSystemdUserUnit(string home, string binary, string stateDir)
    {
        var path = FilePath(home, "linux");
        return File.Exists(path) && RunsDaemon(File.ReadAllText(path), "linux", binary, stateDir);
    }

    /// <summary>
    /// The launchd service (gui/UID/label) to kickstart when the LaunchAgent runs this binary on this state
    /// directory and is loaded, so a lazily started daemon runs under launchd rather than as the client's child.
    /// </summary>
    internal static string? LaunchdService(string home, string binary, string stateDir,
        Func<string, IReadOnlyList<string>, (int ExitCode, string Output)> commandRunner)
    {
        var path = FilePath(home, "macos");
        return File.Exists(path) && RunsDaemon(File.ReadAllText(path), "macos", binary, stateDir)
            && LaunchdDomain(commandRunner) is { } domain && LoadedJob(domain, commandRunner) is { } loadedJob
            && OwnsLoadedJob(loadedJob, binary, stateDir, previous: null)
            ? domain + "/" + MacLabel : null;
    }

    // The file may spell a path through a symlink (HOME=/tmp/x on macOS is /private/tmp/x),
    // so its daemon arguments are compared link-resolved rather than as text.
    static bool RunsDaemon(string content, string platform, string binary, string stateDir)
    {
        if (platform == "linux")
        {
            return content.Split('\n').Select(line => ExecStartPattern().Match(line.Trim())).FirstOrDefault(match => match.Success) is { } unit
                && CanonicalPath.Same(SystemdUnquote(unit.Groups[1].Value), binary)
                && CanonicalPath.Same(SystemdUnquote(unit.Groups[2].Value), stateDir);
        }
        var plist = PlistArgumentsPattern().Match(content);
        return plist.Success
            && CanonicalPath.Same(XmlUnescape(plist.Groups[1].Value), binary)
            && CanonicalPath.Same(XmlUnescape(plist.Groups[2].Value), stateDir);
    }

    static string SystemdUnquote(string value) =>
        SystemdEscapePattern().Replace(value, match => match.Groups[1].Success ? match.Groups[1].Value : match.Value[..1]);

    static string XmlUnescape(string value) => value.Replace("&lt;", "<", StringComparison.Ordinal)
        .Replace("&gt;", ">", StringComparison.Ordinal).Replace("&quot;", "\"", StringComparison.Ordinal)
        .Replace("&apos;", "'", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);

    [GeneratedRegex("""^ExecStart="((?:[^"\\]|\\.)*)" daemon --state-dir "((?:[^"\\]|\\.)*)"$""")]
    private static partial Regex ExecStartPattern();

    [GeneratedRegex(@"\\(.)|%%|\$\$")]
    private static partial Regex SystemdEscapePattern();

    [GeneratedRegex(@"<string>([^<]*)</string><string>daemon</string>\s*<string>--state-dir</string><string>([^<]*)</string>")]
    private static partial Regex PlistArgumentsPattern();

    internal static string LinuxUnit(string binary, string stateDir, string searchPath) => $"""
        [Unit]
        Description=AgentTeamForge daemon

        [Service]
        Type=simple
        UMask=0077
        KillMode=process
        Environment={SystemdQuote("PATH=" + searchPath, command: false)}
        ExecStart={SystemdQuote(binary)} daemon --state-dir {SystemdQuote(stateDir)}

        [Install]
        WantedBy=default.target
        """ + "\n";

    internal static string MacPlist(string binary, string stateDir, string searchPath) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>Label</key><string>{MacLabel}</string>
          <key>ProgramArguments</key><array>
            <string>{SecurityElement.Escape(binary)}</string><string>daemon</string>
            <string>--state-dir</string><string>{SecurityElement.Escape(stateDir)}</string>
          </array>
          <key>EnvironmentVariables</key><dict><key>PATH</key><string>{SecurityElement.Escape(searchPath)}</string></dict>
          <key>RunAtLoad</key><true/>
        </dict></plist>
        """ + "\n";

    static string SystemdQuote(string value, bool command = true)
    {
        if (value.Contains('\n') || value.Contains('\r'))
        {
            throw new ArgumentException("newline in autostart path");
        }
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal).Replace("$", command ? "$$" : "$", StringComparison.Ordinal) + "\"";
    }

    static string WindowsQuote(string value)
    {
        var result = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    static string Platform() => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    [SupportedOSPlatform("windows")]
    static bool WindowsInstalled() => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("AgentTeamForge") is string;

    [SupportedOSPlatform("windows")]
    static int ApplyWindows(string binary, string stateDir, bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enable)
        {
            var launcher = Path.Combine(stateDir, "autostart.vbs");
            var command = $"{WindowsQuote(binary)} daemon --state-dir {WindowsQuote(stateDir)}";
            var script = $"Set shell = CreateObject(\"WScript.Shell\")\r\n" +
                $"shell.Environment(\"PROCESS\")(\"ATF_DAEMON_LOG\") = \"{Path.Combine(stateDir, "daemon.log").Replace("\"", "\"\"", StringComparison.Ordinal)}\"\r\n" +
                $"shell.Run \"{command.Replace("\"", "\"\"", StringComparison.Ordinal)}\", 0, False\r\n";
            var previous = File.Exists(launcher) ? File.ReadAllText(launcher) : null;
            try
            {
                // WSH reads BOM-less scripts in the ANSI code page; UTF-16 keeps non-ASCII profile paths intact.
                File.WriteAllText(launcher, script, System.Text.Encoding.Unicode);
                key.SetValue("AgentTeamForge", $"wscript.exe //B //Nologo {WindowsQuote(launcher)}");
            }
            catch
            {
                if (previous is null)
                {
                    File.Delete(launcher);
                }
                else
                {
                    File.WriteAllText(launcher, previous, System.Text.Encoding.Unicode);
                }
                throw;
            }
        }
        else
        {
            key.DeleteValue("AgentTeamForge", throwOnMissingValue: false);
            var launcher = Path.Combine(stateDir, "autostart.vbs");
            if (File.Exists(launcher))
            {
                File.Delete(launcher);
            }
        }
        Console.Out.WriteLine($"Login autostart: {(enable ? "on" : "off")}");
        return 0;
    }
}
