using System.Globalization;

namespace AgentTeamForge.Business.Features.Wake;

/// <summary>Channel metadata supplied by the recipient's bridge, never by model arguments.
/// For Claude, the existing kind-specific Home field stores the owning host PID.</summary>
public static class ClaudeChannel
{
    public static string Platform => OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "unsupported";

    public static string? Transport(string platform) => platform switch
    {
        "windows" => "pipe",
        "linux" or "macos" => "unix",
        _ => null
    };

    public static string? PipeName(string? address)
    {
        const string prefix = @"\\.\pipe\";
        if (address is null || !address.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { return null; }
        var name = address[prefix.Length..];
        return name.Length is > 0 and <= 256 && name != "anonymous"
            && !name.Any(c => c is '\\' or '/' || char.IsControl(c)) ? name : null;
    }

    public static bool Valid(string? address, string? secret, string? host, string? platform = null)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 4096
            || string.IsNullOrWhiteSpace(secret) || secret.Length > 4096
            || !int.TryParse(host, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            return false;
        }
        var transport = Transport(platform ?? Platform);
        if (transport == "pipe") { return PipeName(address) is not null; }
        if (transport != "unix" || !address.StartsWith('/') || address.Contains('\0')) { return false; }
        var name = Path.GetFileName(address);
        return !name.EndsWith(".sock", StringComparison.Ordinal)
            || !int.TryParse(name[..^5], out var socketPid) || socketPid == pid;
    }
}
