using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>Durable ownership proof for Herdr sessions left alive across interrupted turns.</summary>
public static class HerdrOwnedSessions
{
    internal static string PathFor(InteractiveLaunch launch) => System.IO.Path.ChangeExtension(launch.BootstrapPath, ".owned.json");

    internal static void Save(InteractiveLaunch launch, OwnedHerdrSession session)
    {
        var path = PathFor(launch);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(session, HerdrSessionJson.Default.OwnedHerdrSession));
        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
    }

    internal static void Delete(InteractiveLaunch launch) => File.Delete(PathFor(launch));

    /// <summary>
    /// Runs before claims. A record whose ownership cannot be proven (or a Herdr fault) is
    /// logged and kept for the next start; the session is never touched and startup continues.
    /// </summary>
    public static void Recover(string stateRoot, Func<OwnedHerdrSession, Task> stop, Action<string> log)
    {
        var directory = System.IO.Path.Combine(stateRoot, "herdr");
        if (!Directory.Exists(directory))
        {
            return;
        }
        foreach (var path in Directory.EnumerateFiles(directory, "*.owned.json"))
        {
            try
            {
                var session = JsonSerializer.Deserialize(File.ReadAllText(path), HerdrSessionJson.Default.OwnedHerdrSession)
                    ?? throw new HerdrLaunchException("invalid Herdr ownership record");
                stop(session).GetAwaiter().GetResult(); // Re-proves PID, start time and owner label; absent sessions return.
                File.Delete(path);
            }
            catch (Exception e)
            {
                log($"warning: Herdr session recovery skipped for {path}: {e.Message}");
            }
        }
    }
}

[JsonSerializable(typeof(OwnedHerdrSession))]
internal partial class HerdrSessionJson : JsonSerializerContext;
