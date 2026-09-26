using System.Text;
using System.Text.Json;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>
/// Reads the same native JSONL session stores used by win-agent-teams/agent_output.py.
/// A correlation marker in the submitted prompt binds a turn before its text is returned.
/// </summary>
internal sealed class InteractiveTranscriptReader : IInteractiveTranscriptReader
{
    const int MaxTranscriptBytes = 32 * 1024 * 1024;
    const int MaxResultChars = 32 * 1024;

    public InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started)
    {
        var candidates = Files(launch)
            .Where(p => Modified(p) >= started.UtcDateTime.AddSeconds(-2))
            .OrderByDescending(Modified)
            .Take(200);
        InteractiveTranscript? bound = null;
        foreach (var path in candidates)
        {
            var parsed = Parse(path, launch.Kind, correlationMarker);
            if (parsed is null)
            {
                continue;
            }
            if (launch.ResumeSessionId is { } expected && parsed.SessionId != expected)
            {
                continue;
            }
            if (bound is not null)
            {
                return null; // Ambiguous: never attribute another agent's output.
            }
            bound = parsed;
        }
        return bound;
    }

    public string? FindPiSessionDirectory(string root, string sessionId)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
            {
                if (HeaderId(file, InteractiveAgentKind.Pi) == sessionId)
                {
                    return dir;
                }
            }
        }
        return null;
    }

    static IEnumerable<string> Files(InteractiveLaunch launch)
    {
        if (launch.Kind == InteractiveAgentKind.Pi)
        {
            return Directory.Exists(launch.PiSessionDirectory) ? Directory.EnumerateFiles(launch.PiSessionDirectory!, "*.jsonl") : [];
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (launch.Kind == InteractiveAgentKind.Claude)
        {
            var cwd = Path.GetFullPath(launch.WorkingDirectory);
            var encoded = new string([.. cwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
            var dir = Path.Combine(home, ".claude", "projects", encoded);
            return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.jsonl") : [];
        }
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex");
        var sessions = Path.Combine(codexHome, "sessions");
        return Directory.Exists(sessions) ? Directory.EnumerateFiles(sessions, "rollout-*.jsonl", SearchOption.AllDirectories) : [];
    }

    static DateTime Modified(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (IOException) { return DateTime.MinValue; }
    }

    static string? HeaderId(string path, InteractiveAgentKind kind)
    {
        try
        {
            foreach (var line in File.ReadLines(path).Take(10))
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (kind == InteractiveAgentKind.Pi && Str(root, "type") == "session")
                {
                    return Str(root, "id");
                }
                if (kind == InteractiveAgentKind.Codex && Str(root, "type") == "session_meta" && root.TryGetProperty("payload", out var payload))
                {
                    return Str(payload, "id");
                }
                if (kind == InteractiveAgentKind.Claude && Str(root, "sessionId") is { } id)
                {
                    return id;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return null;
    }

    static InteractiveTranscript? Parse(string path, InteractiveAgentKind kind, string marker)
    {
        try
        {
            if (new FileInfo(path).Length > MaxTranscriptBytes)
            {
                return null;
            }
            var id = HeaderId(path, kind);
            if (id is null)
            {
                return null;
            }
            var markerSeen = false;
            string? last = null;
            var progress = new List<string>();
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (line.Contains(marker, StringComparison.Ordinal))
                {
                    markerSeen = true;
                    last = null;
                    continue;
                }
                if (!markerSeen)
                {
                    continue;
                }
                using var json = JsonDocument.Parse(line);
                if (AssistantText(json.RootElement, kind) is { } text)
                {
                    last = text;
                    progress.Add(text);
                }
            }
            return markerSeen ? new(id, last is { Length: > MaxResultChars } ? last[^MaxResultChars..] : last, progress) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    static string? AssistantText(JsonElement root, InteractiveAgentKind kind)
    {
        JsonElement message;
        if (kind == InteractiveAgentKind.Codex)
        {
            if (Str(root, "type") != "response_item" || !root.TryGetProperty("payload", out message))
            {
                return null;
            }
        }
        else if (kind == InteractiveAgentKind.Pi)
        {
            if (Str(root, "type") != "message" || !root.TryGetProperty("message", out message))
            {
                return null;
            }
        }
        else
        {
            if (Str(root, "type") != "assistant" || !root.TryGetProperty("message", out message))
            {
                return null;
            }
        }
        if (Str(message, "role") != "assistant" || !message.TryGetProperty("content", out var content))
        {
            return null;
        }
        if (content.ValueKind == JsonValueKind.String && kind == InteractiveAgentKind.Claude)
        {
            return content.GetString();
        }
        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var wanted = kind == InteractiveAgentKind.Codex ? "output_text" : "text";
        var parts = content.EnumerateArray()
            .Where(item => Str(item, "type") == wanted)
            .Select(item => Str(item, "text"))
            .Where(text => text is not null);
        var joined = string.Concat(parts);
        return joined.Length > 0 ? joined : null;
    }

    static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
