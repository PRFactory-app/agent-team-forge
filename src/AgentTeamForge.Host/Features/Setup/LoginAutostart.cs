using System.Security;
using System.Runtime.Versioning;
using Microsoft.Win32;
using AgentTeamForge.Host.Hosting;

namespace AgentTeamForge.Host.Features.Setup;

/// <summary>Optional user login registration. Paths are injectable for tests.</summary>
public static class LoginAutostart
{
    const string Name = "agentteamforge";
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
        if (!enable && platform == "linux" && File.Exists(path))
        {
            var (code, output) = commandRunner("systemctl", ["--user", "disable", Name + ".service"]);
            if (code != 0)
            {
                Console.Error.WriteLine($"error: systemd user autostart disable failed: {output}");
                return 1;
            }
        }
        if (enable)
        {
            StateDirectory.CreatePrivateDirectory(Path.GetDirectoryName(path)!);
            var content = platform == "linux" ? LinuxUnit(binary, stateDir) : MacPlist(binary, stateDir);
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

        if (platform == "linux")
        {
            var (reloadCode, reloadOutput) = commandRunner("systemctl", ["--user", "daemon-reload"]);
            var (enableCode, enableOutput) = enable ? commandRunner("systemctl", ["--user", "enable", Name + ".service"]) : (0, "");
            if (reloadCode != 0 || enableCode != 0)
            {
                Console.Error.WriteLine($"error: systemd user autostart {(enable ? "enable" : "disable")} failed: {reloadOutput} {enableOutput}".Trim());
                return 1;
            }
        }
        Console.Out.WriteLine($"Login autostart: {(enable ? "on" : "off")}");
        return 0;
    }

    internal static string FilePath(string home, string platform) => platform switch
    {
        "linux" => Path.Combine(home, ".config", "systemd", "user", Name + ".service"),
        "macos" => Path.Combine(home, "Library", "LaunchAgents", "com.agentteamforge.daemon.plist"),
        _ => throw new PlatformNotSupportedException(platform),
    };

    internal static string LinuxUnit(string binary, string stateDir) => $"""
        [Unit]
        Description=AgentTeamForge daemon

        [Service]
        Type=simple
        ExecStart={SystemdQuote(binary)} daemon --state-dir {SystemdQuote(stateDir)}

        [Install]
        WantedBy=default.target
        """ + "\n";

    internal static string MacPlist(string binary, string stateDir) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>Label</key><string>com.agentteamforge.daemon</string>
          <key>ProgramArguments</key><array>
            <string>{SecurityElement.Escape(binary)}</string><string>daemon</string>
            <string>--state-dir</string><string>{SecurityElement.Escape(stateDir)}</string>
          </array>
          <key>RunAtLoad</key><true/>
        </dict></plist>
        """ + "\n";

    static string SystemdQuote(string value)
    {
        if (value.Contains('\n') || value.Contains('\r'))
        {
            throw new ArgumentException("newline in autostart path");
        }
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal).Replace("$", "$$", StringComparison.Ordinal) + "\"";
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
            key.SetValue("AgentTeamForge", $"{WindowsQuote(binary)} daemon --state-dir {WindowsQuote(stateDir)}");
        }
        else
        {
            key.DeleteValue("AgentTeamForge", throwOnMissingValue: false);
        }
        Console.Out.WriteLine($"Login autostart: {(enable ? "on" : "off")}");
        return 0;
    }
}
