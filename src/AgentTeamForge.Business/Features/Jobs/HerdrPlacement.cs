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
        if (!Valid(value)) { throw new ArgumentException(Invalid(value)); }
        return value;
    }

    public void Change(string value)
    {
        if (!Valid(value)) { throw new ArgumentException(Invalid(value)); }
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

    public const string OwnSessionRemoved = "own-session is no longer supported; use herdr-session:<name>";

    static string Invalid(string? value) => value == "own-session" ? OwnSessionRemoved : "Invalid Herdr placement; use herdr-session:<name>";

    /// <summary>Placements a new job or a saved default may use. Legacy <c>own-session</c> lives on only in old jobs' options.</summary>
    public static bool Valid(string? value) => value is { Length: > 14 and <= 78 }
        && value.StartsWith("herdr-session:", StringComparison.Ordinal)
        && value[14..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    static string Load(string path, Action<string>? log)
    {
        try
        {
            if (!File.Exists(path)) { return ConfiguredDefault(log); }
            var value = JsonSerializer.Deserialize(File.ReadAllText(path), HerdrPlacementJson.Default.HerdrPlacementFile)?.Placement;
            if (Valid(value)) { return value!; }
            if (value == "own-session")
            {
                log?.Invoke($"warning: {path} holds the removed own-session placement; using the configured Herdr session");
                return ConfiguredDefault(log);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            log?.Invoke($"warning: ignoring unreadable {path}: {e.Message}");
            return ConfiguredDefault(log);
        }
        log?.Invoke($"warning: ignoring invalid {path}; using configured Herdr session");
        return ConfiguredDefault(log);
    }

    static string ConfiguredDefault(Action<string>? log)
    {
        var name = Environment.GetEnvironmentVariable("ATF_HERDR_SESSION")?.Trim();
        var placement = "herdr-session:" + (string.IsNullOrEmpty(name) ? "default" : name);
        if (Valid(placement)) { return placement; }
        log?.Invoke("warning: ignoring invalid ATF_HERDR_SESSION (letters, digits, '-' or '_', at most 64); using herdr-session:default");
        return "herdr-session:default";
    }
}

internal sealed record HerdrPlacementFile(string Placement);
[JsonSerializable(typeof(HerdrPlacementFile))]
internal partial class HerdrPlacementJson : JsonSerializerContext;
