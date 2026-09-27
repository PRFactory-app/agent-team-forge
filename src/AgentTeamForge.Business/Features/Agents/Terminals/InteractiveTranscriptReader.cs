using System.Collections.Concurrent;
using System.Text.Json;
using AgentTeamForge.DAL.Files;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>
/// Reads the same native JSONL session stores used by win-agent-teams/agent_output.py.
/// A correlation marker in the submitted prompt binds a turn before its text is returned;
/// the turn ends at the next native user/turn boundary, so later human input never alters it.
/// </summary>
/// <param name="environment">The environment the agent was launched with (config roots).</param>
internal sealed class InteractiveTranscriptReader(Func<string, string?> environment) : IInteractiveTranscriptReader
{
    public InteractiveTranscriptReader() : this(Environment.GetEnvironmentVariable) { }

    readonly string _codexHome = CodexPaths.Home(environment, Environment.CurrentDirectory);

    const int MaxTranscriptBytes = 32 * 1024 * 1024;
    const int MaxResultChars = 32 * 1024;

    /// <summary>Scan only the frozen Codex thread, including after daemon restart.</summary>
    internal static InteractiveTranscript? ReadCodexThread(string home, string thread, string correlation)
    {
        var sessions = Path.Combine(home, "sessions");
        if (!Directory.Exists(sessions)) { return null; }
        try
        {
            foreach (var path in Directory.EnumerateFiles(sessions, "rollout-*-" + thread + ".jsonl", SearchOption.AllDirectories))
            {
                if (HeaderId(path, InteractiveAgentKind.Codex) == thread && IsParent(path, InteractiveAgentKind.Codex) == true)
                {
                    return Parse(path, InteractiveAgentKind.Codex, "atf-corr:" + correlation);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    public InteractiveTranscript? Read(InteractiveLaunch launch, string correlationMarker, DateTimeOffset started)
    {
        if (launch.NativeTranscript is { } retained)
        {
            var current = HeaderId(retained.Path, launch.Kind);
            if (current is null && File.Exists(retained.Path))
            {
                return null; // Transiently unreadable: keep polling rather than declare the binding lost.
            }
            if (current != retained.SessionId)
            {
                return new(retained.SessionId, null, BindingError: "interactive_binding_lost");
            }
            return Parse(retained.Path, launch.Kind, correlationMarker);
        }
        var candidates = Files(launch).Where(p => Modified(p) >= started.UtcDateTime.AddSeconds(-2));
        InteractiveTranscript? bound = null;
        string? boundPath = null;
        var unverified = false;
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
            var parent = IsParent(path, launch.Kind);
            if (parent is null) { unverified = true; }
            if (parent != true) { continue; }
            if (bound is not null)
            {
                return new("", null, BindingError: "interactive_binding_ambiguous");
            }
            bound = parsed;
            boundPath = path;
        }
        if (unverified) { return new("", null, BindingError: "interactive_binding_unverified"); }
        if (bound is not null) { launch.NativeTranscript = new(bound.SessionId, boundPath!); }
        return bound;
    }

    // Native ancestry, not file recency, distinguishes a TUI from inherited child history.
    static bool? IsParent(string path, InteractiveAgentKind kind)
    {
        try
        {
            // Codex/Pi carry ancestry in their header; Claude records it on every chain record
            // (system/attachment/message), which can follow a long preamble of metadata lines.
            var lines = LiveFiles.ReadLines(path);
            foreach (var line in kind == InteractiveAgentKind.Claude ? lines : lines.Take(10))
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (kind == InteractiveAgentKind.Codex && Str(root, "type") == "session_meta"
                    && root.TryGetProperty("payload", out var meta))
                {
                    if (Str(meta, "source") == "cli") { return true; }
                    if (meta.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object
                        && source.TryGetProperty("subagent", out _)) { return false; }
                    return null;
                }
                if (kind == InteractiveAgentKind.Claude && root.TryGetProperty("isSidechain", out var sidechain)
                    && sidechain.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return !sidechain.GetBoolean();
                }
                if (kind == InteractiveAgentKind.Pi && Str(root, "type") == "session")
                {
                    return Str(root, "parentSession") is null;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { WarnUnreadable(path, e); }
        catch (JsonException) { }
        return null;
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

    IEnumerable<string> Files(InteractiveLaunch launch)
    {
        if (launch.Kind == InteractiveAgentKind.Pi)
        {
            return Directory.Exists(launch.PiSessionDirectory) ? Directory.EnumerateFiles(launch.PiSessionDirectory!, "*.jsonl") : [];
        }

        if (launch.Kind == InteractiveAgentKind.Claude)
        {
            var cwd = Path.GetFullPath(launch.WorkingDirectory);
            var encoded = new string([.. cwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
            var dir = Path.Combine(ClaudeConfigRoot.Resolve(environment, cwd), "projects", encoded);
            return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.jsonl") : [];
        }
        var sessions = Path.Combine(_codexHome, "sessions");
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
            foreach (var line in LiveFiles.ReadLines(path).Take(10))
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { WarnUnreadable(path, e); }
        catch (JsonException) { }
        return null;
    }

    static readonly ConcurrentDictionary<string, DateTime> Warned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An unreadable transcript silently stalls a job, so say so in the daemon log (once per file per 5 min).</summary>
    static void WarnUnreadable(string path, Exception e)
    {
        var now = DateTime.UtcNow;
        if (Warned.TryGetValue(path, out var last) && now - last < TimeSpan.FromMinutes(5))
        {
            return;
        }
        Warned[path] = now;
        Console.Error.WriteLine($"[atf-daemon] transcript unreadable: {path}: {e.GetType().Name}: {e.Message}");
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
            var completed = false;
            var ended = false;
            string? last = null;
            InteractiveApiError? apiError = null;
            var progress = new List<string>();
            var backgroundTools = new HashSet<string>();
            var backgroundTasks = new HashSet<string>();
            var knownTasks = new HashSet<string>();
            foreach (var line in LiveFiles.ReadLines(path))
            {
                JsonDocument json;
                try { json = JsonDocument.Parse(line); }
                catch (JsonException)
                {
                    // Unreadable (e.g. partially flushed): it could be the next turn's boundary,
                    // so nothing after it is attributed to this turn.
                    if (markerSeen)
                    {
                        break;
                    }
                    continue;
                }
                using (json)
                {
                    var root = json.RootElement;
                    var userText = UserText(root, kind);
                    if (!markerSeen)
                    {
                        // Bind only on a native user record; echoes and metadata never bind.
                        markerSeen = userText?.Contains(marker, StringComparison.Ordinal) == true;
                        continue;
                    }
                    if (kind == InteractiveAgentKind.Claude && Str(root, "type") == "user"
                        && root.TryGetProperty("message", out var notificationMessage)
                        && notificationMessage.TryGetProperty("content", out var notificationContent)
                        && notificationContent.ValueKind == JsonValueKind.String
                        && TaskNotification(notificationContent.GetString()!, backgroundTasks, knownTasks, out var wasPending))
                    {
                        // The notification starts a continuation, not a human turn. Its
                        // own final assistant record must arrive before completion.
                        if (wasPending) { completed = false; }
                        if (wasPending) { apiError = null; }
                        continue;
                    }
                    if (userText is not null)
                    {
                        // Codex records the same input as both response_item and event_msg.
                        if (kind == InteractiveAgentKind.Codex && userText.Contains(marker, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        ended = true;
                        break; // Next native user input: a new turn.
                    }
                    if (kind == InteractiveAgentKind.Codex && EventType(root) == "task_started")
                    {
                        break;
                    }
                    if (kind == InteractiveAgentKind.Claude)
                    {
                        TrackBackgroundTasks(root, backgroundTools, backgroundTasks, knownTasks);
                        if (Str(root, "type") == "assistant")
                        {
                            completed = CompletedTurn(root, kind) && backgroundTools.Count == 0 && backgroundTasks.Count == 0;
                            apiError = ApiError(root);
                        }
                        else if (apiError is not null && Str(root, "type") == "system" && Str(root, "subtype") == "turn_duration")
                        {
                            apiError = apiError with { TurnEnded = true };
                        }
                    }
                    else { completed |= CompletedTurn(root, kind); }
                    if (AssistantText(root, kind) is { } text)
                    {
                        last = text;
                        progress.Add(text);
                    }
                }
            }
            return markerSeen ? new(id, last is { Length: > MaxResultChars } ? last[^MaxResultChars..] : last, progress, completed,
                !ended && (backgroundTools.Count > 0 || backgroundTasks.Count > 0),
                ApiError: backgroundTools.Count == 0 && backgroundTasks.Count == 0 ? apiError : null) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            WarnUnreadable(path, e);
            return null;
        }
    }

    static InteractiveApiError? ApiError(JsonElement root)
    {
        if (!Flag(root, "isApiErrorMessage") || !root.TryGetProperty("message", out var message)) { return null; }
        var error = Str(root, "error") ?? Str(message, "error");
        var text = AssistantText(root, InteractiveAgentKind.Claude);
        if (text is { Length: > MaxResultChars }) { text = text[^MaxResultChars..]; }
        var login = error is "authentication_failed" or "authentication_error" or "not_logged_in" or "unauthorized" or "invalid_api_key"
            || text?.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) == true;
        return login
            ? new("agent_login_required", "Claude is not logged in; run `claude` and /login."
                + (string.IsNullOrWhiteSpace(text) ? "" : " " + text))
            : new("agent_api_error", string.IsNullOrWhiteSpace(text) ? $"Claude API error: {error ?? "unknown"}" : text);
    }

    static void TrackBackgroundTasks(JsonElement root, HashSet<string> tools, HashSet<string> tasks, HashSet<string> knownTasks)
    {
        if (root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (Str(block, "type") == "tool_use" && Str(block, "id") is { } toolId
                    && block.TryGetProperty("input", out var input) && Flag(input, "run_in_background"))
                {
                    tools.Add(toolId);
                }
                if (Str(block, "type") == "tool_result" && Str(block, "tool_use_id") is { } resultId)
                {
                    tools.Remove(resultId);
                }
            }
        }
        // Also covers Bash commands automatically moved to the background by Claude.
        if (root.TryGetProperty("toolUseResult", out var result) && Str(result, "backgroundTaskId") is { } taskId)
        {
            tasks.Add(taskId);
            knownTasks.Add(taskId);
        }
        // An async Agent launch reports back later with a task notification keyed by its agent ID.
        if (Str(result, "status") == "async_launched" && Str(result, "agentId") is { } agentId)
        {
            tasks.Add(agentId);
            knownTasks.Add(agentId);
        }
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("task", out var task)
            && Str(task, "task_id") is { } finishedId && Str(task, "status") is "completed" or "failed" or "killed")
        {
            tasks.Remove(finishedId); // TaskOutput can observe completion before a notification arrives.
        }
        if (Str(result, "task_id") is { } stoppedId && Str(result, "task_type") is not null)
        {
            tasks.Remove(stoppedId); // TaskStop (KillShell): a stopped task is marked notified and never sends a notification.
        }
    }

    static bool TaskNotification(string text, HashSet<string> tasks, HashSet<string> knownTasks, out bool wasPending)
    {
        wasPending = false;
        if (!text.TrimStart().StartsWith("<task-notification>", StringComparison.Ordinal)) { return false; }
        var start = text.IndexOf("<task-id>", StringComparison.Ordinal);
        var end = text.IndexOf("</task-id>", StringComparison.Ordinal);
        if (start < 0 || end < start + 9) { return false; }
        var id = text[(start + 9)..end];
        if (!knownTasks.Contains(id)) { return false; }
        wasPending = tasks.Contains(id);
        if (text.Contains("<status>completed</status>", StringComparison.Ordinal)
            || text.Contains("<status>failed</status>", StringComparison.Ordinal)
            || text.Contains("<status>killed</status>", StringComparison.Ordinal))
        {
            tasks.Remove(id);
        }
        return true;
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

    /// <summary>The text of a native user-input record, or null for any other record.</summary>
    static string? UserText(JsonElement root, InteractiveAgentKind kind)
    {
        JsonElement message;
        if (kind == InteractiveAgentKind.Codex)
        {
            if (EventType(root) == "user_message" && root.TryGetProperty("payload", out var payload))
            {
                return Str(payload, "message") ?? "";
            }
            if (Str(root, "type") != "response_item" || !root.TryGetProperty("payload", out message) || Str(message, "type") != "message")
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
            // Tool results, meta and compaction records are also "user" entries but stay in the turn.
            if (Str(root, "type") != "user" || Flag(root, "isMeta") || Flag(root, "isCompactSummary") || !root.TryGetProperty("message", out message))
            {
                return null;
            }
        }
        if (Str(message, "role") != "user" || !message.TryGetProperty("content", out var content))
        {
            return null;
        }
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }
        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        if (kind == InteractiveAgentKind.Claude && content.EnumerateArray().Any(item => Str(item, "type") == "tool_result"))
        {
            return null; // A tool result is part of the running turn, not user input.
        }
        // Image-only (or otherwise text-less) input is still a user turn boundary.
        var wanted = kind == InteractiveAgentKind.Codex ? "input_text" : "text";
        return string.Concat(content.EnumerateArray().Where(item => Str(item, "type") == wanted).Select(item => Str(item, "text")));
    }

    static string? EventType(JsonElement root) =>
        Str(root, "type") == "event_msg" && root.TryGetProperty("payload", out var payload) ? Str(payload, "type") : null;

    static bool CompletedTurn(JsonElement root, InteractiveAgentKind kind)
    {
        if (kind == InteractiveAgentKind.Codex)
        {
            return Str(root, "type") == "event_msg" && root.TryGetProperty("payload", out var payload)
                && Str(payload, "type") == "task_complete";
        }
        if (kind == InteractiveAgentKind.Claude)
        {
            // Claude writes each content block as its own record; a thinking-only end_turn
            // record can precede the final text, so only a text-bearing one completes the turn.
            return Str(root, "type") == "assistant" && root.TryGetProperty("message", out var message)
                && Str(message, "stop_reason") == "end_turn" && AssistantText(root, kind) is not null;
        }
        return Str(root, "type") == "message" && root.TryGetProperty("message", out var piMessage)
            && Str(piMessage, "role") == "assistant" && Str(piMessage, "stopReason") is "stop" or "end_turn";
    }

    static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
