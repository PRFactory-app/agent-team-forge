using System.Collections;

namespace AgentTeamForge.Host.Features.Setup;

internal static class DaemonEnvironment
{
    // A daemon can outlive the lead that started it. Carry only user-session
    // plumbing, not the lead's agent identity, hooks, or one-off credentials.
    internal static void Scrub(IDictionary<string, string?> environment)
    {
        foreach (var key in environment.Keys.ToArray())
        {
            if (!Keep(key))
            {
                environment.Remove(key);
            }
        }
    }

    internal static bool Keep(string key)
    {
        var name = key.ToUpperInvariant();
        if (name.StartsWith("LC_", StringComparison.Ordinal) || name.StartsWith("XDG_", StringComparison.Ordinal)
            || name.EndsWith("_PROXY", StringComparison.Ordinal))
        {
            return true;
        }
        return name is "PATH" or "HOME" or "LANG" or "LANGUAGE" or "USER" or "LOGNAME" or "SHELL"
            or "TMPDIR" or "TMP" or "TEMP" or "TERM" or "COLORTERM" or "DISPLAY" or "WAYLAND_DISPLAY"
            or "DBUS_SESSION_BUS_ADDRESS" or "XAUTHORITY" or "SSH_AUTH_SOCK" or "NO_PROXY"
            or "SYSTEMROOT" or "WINDIR" or "COMSPEC" or "PATHEXT" or "USERPROFILE" or "APPDATA"
            or "LOCALAPPDATA" or "PROGRAMDATA" or "PROGRAMFILES" or "PROGRAMFILES(X86)"
            or "COMMONPROGRAMFILES" or "COMMONPROGRAMFILES(X86)" or "PROCESSOR_ARCHITECTURE"
            or "DOTNET_ROOT" or "DOTNET_ROOT_X64" or "DOTNET_ROOT_X86";
    }

    internal static Dictionary<string, string?> Current()
    {
        var result = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        Scrub(result);
        return result;
    }
}
