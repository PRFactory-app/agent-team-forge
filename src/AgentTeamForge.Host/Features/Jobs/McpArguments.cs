using System.Globalization;
using System.Text.Json;

namespace AgentTeamForge.Host.Features.Jobs;

/// <summary>
/// Checks tool arguments against the tool's advertised input schema before they are mapped, so a missing
/// required field, a wrong JSON type or an out-of-range value is rejected naming the field instead of
/// being read as absent (a numeric job_id used to become an empty id and a misleading not_found).
/// Supports the subset the bridge's schemas use: required, dependentRequired, additionalProperties:false,
/// and per-property type, enum, minimum, maximum, maxItems and string items. A JSON null is left to the
/// tool's own mapping, which already treats it per field.
/// </summary>
internal static class McpArguments
{
    /// <summary>The first problem as a sentence naming the field, or null when the arguments fit the schema.</summary>
    internal static string? Invalid(JsonElement schema, IDictionary<string, JsonElement> args)
    {
        var properties = schema.TryGetProperty("properties", out var declared) && declared.ValueKind == JsonValueKind.Object
            ? declared : default;
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray().Select(n => n.GetString()!))
            {
                if (!Present(args, name)) { return $"Missing required argument {name}."; }
            }
        }

        if (schema.TryGetProperty("dependentRequired", out var dependent) && dependent.ValueKind == JsonValueKind.Object)
        {
            foreach (var rule in dependent.EnumerateObject().Where(rule => Present(args, rule.Name)))
            {
                foreach (var name in rule.Value.EnumerateArray().Select(n => n.GetString()!))
                {
                    if (!Present(args, name)) { return $"Missing argument {name}: it is required with {rule.Name}."; }
                }
            }
        }

        var closed = schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False;
        foreach (var (name, value) in args)
        {
            if (properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(name, out var property))
            {
                if (closed) { return $"Unknown argument {name}."; }
                continue;
            }

            if (value.ValueKind != JsonValueKind.Null && InvalidValue(name, property, value) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    static bool Present(IDictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var value) && value.ValueKind != JsonValueKind.Null;

    static string? InvalidValue(string name, JsonElement property, JsonElement value)
    {
        var type = property.TryGetProperty("type", out var declared) ? declared.GetString() : null;
        switch (type)
        {
            case "string":
                if (value.ValueKind != JsonValueKind.String) { return $"{name} must be a string."; }
                if (property.TryGetProperty("enum", out var allowed)
                    && allowed.EnumerateArray().All(option => option.GetString() != value.GetString()))
                {
                    return $"{name} must be one of: {string.Join(", ", allowed.EnumerateArray().Select(option => option.GetString()))}.";
                }
                return null;
            case "integer":
                var minimum = Bound(property, "minimum");
                var maximum = Bound(property, "maximum");
                return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
                    && (minimum is null || number >= minimum) && (maximum is null || number <= maximum)
                    ? null
                    : $"{name} must be an integer{Range(minimum, maximum)}.";
            case "boolean":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : $"{name} must be true or false.";
            case "array":
                if (value.ValueKind != JsonValueKind.Array) { return $"{name} must be an array."; }
                if (Bound(property, "maxItems") is { } maxItems && value.GetArrayLength() > maxItems)
                {
                    return $"{name} must have at most {maxItems} items.";
                }
                var itemType = property.TryGetProperty("items", out var items) && items.TryGetProperty("type", out var t) ? t.GetString() : null;
                return itemType == "string" && value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)
                    ? $"{name} must contain only strings." : null;
            default:
                return null;
        }
    }

    static long? Bound(JsonElement property, string keyword) =>
        property.TryGetProperty(keyword, out var bound) && bound.TryGetInt64(out var value) ? value : null;

    static string Range(long? minimum, long? maximum) => (minimum, maximum) switch
    {
        (null, null) => "",
        ({ } min, null) => string.Create(CultureInfo.InvariantCulture, $" of at least {min}"),
        (null, { } max) => string.Create(CultureInfo.InvariantCulture, $" of at most {max}"),
        ({ } min, { } max) => string.Create(CultureInfo.InvariantCulture, $" from {min} to {max}"),
    };
}
