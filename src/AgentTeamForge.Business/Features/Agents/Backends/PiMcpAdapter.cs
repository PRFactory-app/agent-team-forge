using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentTeamForge.Business.Features.Agents.Backends;

public static class PiMcpAdapter
{
    public const string Package = "npm:pi-mcp-adapter";
    public const string InstallHint = "Pi MCP adapter missing; run atf setup or pi install npm:pi-mcp-adapter.";

    public static bool IsInstalled(string? home = null)
    {
        home ??= Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var settingsPath = Path.Combine(home, ".pi", "agent", "settings.json");
        try
        {
            if (!File.Exists(settingsPath)) { return false; }
            var settings = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
            return settings?["packages"] is JsonArray packages && packages.Any(entry => PackageSource(entry) == Package);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    // Pi accepts package entries as a source string or an object with a source string.
    public static string? PackageSource(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var source) => source,
        JsonObject entry when entry["source"] is JsonValue value && value.TryGetValue<string>(out var source) => source,
        _ => null,
    };
}
