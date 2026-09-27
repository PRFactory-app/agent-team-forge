using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>One state directory's default Herdr placement.</summary>
public sealed class HerdrPlacement(string statePath, Action<string>? log = null)
{
    public string StatePath => statePath;
    readonly string _path = Path.Combine(statePath, "herdr-placement.json");
    readonly Lock _gate = new();
    public string Default { get; private set; } = Load(Path.Combine(statePath, "herdr-placement.json"), log);

    public string Resolve(string? requested)
    {
        var value = requested ?? Default;
        if (!Valid(value)) { throw new ArgumentException("Invalid Herdr placement; use own-session or herdr-session:<name>"); }
        return value;
    }

    public void Change(string value)
    {
        if (!Valid(value)) { throw new ArgumentException("Invalid Herdr placement; use own-session or herdr-session:<name>"); }
        lock (_gate)
        {
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                    JsonSerializer.Serialize(stream, new HerdrPlacementFile(value), HerdrPlacementJson.Default.HerdrPlacementFile);
                    stream.Flush(true);
                }
                File.Move(temp, _path, true);
                Default = value;
            }
            finally { if (File.Exists(temp)) { File.Delete(temp); } }
        }
    }

    public static bool Valid(string? value) => value == "own-session" || value is { Length: > 14 and <= 78 }
        && value.StartsWith("herdr-session:", StringComparison.Ordinal)
        && value[14..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    static string Load(string path, Action<string>? log)
    {
        try
        {
            if (!File.Exists(path)) { return ConfiguredDefault(); }
            var value = JsonSerializer.Deserialize(File.ReadAllText(path), HerdrPlacementJson.Default.HerdrPlacementFile)?.Placement;
            if (Valid(value)) { return value!; }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            log?.Invoke($"warning: ignoring unreadable {path}: {e.Message}");
            return ConfiguredDefault();
        }
        log?.Invoke($"warning: ignoring invalid {path}; using configured Herdr session");
        return ConfiguredDefault();
    }

    static string ConfiguredDefault()
    {
        var name = Environment.GetEnvironmentVariable("ATF_HERDR_SESSION")?.Trim();
        var placement = "herdr-session:" + (string.IsNullOrEmpty(name) ? "default" : name);
        return Valid(placement) ? placement : "herdr-session:default";
    }
}

internal sealed record HerdrPlacementFile(string Placement);
[JsonSerializable(typeof(HerdrPlacementFile))]
internal partial class HerdrPlacementJson : JsonSerializerContext;
