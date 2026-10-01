using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AgentTeamForge.Host.Hosting;

namespace AgentTeamForge.Host.Features.Setup;

/// <summary>
/// Reads a private JSON settings file and says which way it is wrong: not JSON at all,
/// not an object, missing required fields, or a field of the wrong type.
/// </summary>
internal static class StateJson
{
    const string Remedy = "run atf setup or atf doctor to inspect configuration";

    internal static T Read<T>(string path, string code, JsonTypeInfo<T> type, params string[] required) where T : class
    {
        var bytes = StateDirectory.ReadPrivateFile(path);
        if (bytes.Length == 0)
        {
            throw new StateDirectoryException(code, $"{path} is empty; {Remedy}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new StateDirectoryException(code, $"invalid JSON in {path} at line {ex.LineNumber + 1}, byte {ex.BytePositionInLine}; {Remedy}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new StateDirectoryException(code, $"{path} must contain a JSON object, not {root.ValueKind.ToString().ToLowerInvariant()}; {Remedy}");
            }

            var missing = required.Where(name => !root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null).ToArray();
            if (missing.Length > 0)
            {
                throw new StateDirectoryException(code, $"{path} is missing required field(s): {string.Join(", ", missing)}; {Remedy}");
            }

            try
            {
                return root.Deserialize(type) ?? throw new JsonException();
            }
            catch (JsonException ex)
            {
                throw new StateDirectoryException(code, $"{path} has a field of the wrong type{(ex.Path is { Length: > 1 } at ? " at " + at : "")}; {Remedy}");
            }
        }
    }
}
