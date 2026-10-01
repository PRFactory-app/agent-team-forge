using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    internal static string? LocateSession(string home, string sessionId, InteractiveAgentKind kind, bool usage = true)
    {
        var root = Path.Combine(home, kind == InteractiveAgentKind.Claude ? "projects" : "sessions");
        if (!Directory.Exists(root)) { return null; }
        var pattern = kind switch
        {
            InteractiveAgentKind.Claude => sessionId + ".jsonl",
            InteractiveAgentKind.Codex => "rollout-*-" + sessionId + ".jsonl",
            _ => "*.jsonl"
        };
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                if (HeaderId(path, kind) == sessionId && IsParent(path, kind, usage: usage) == true) { return path; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    internal static InteractiveTranscript? ReadCodexThread(string home, string thread, string correlation) =>
        LocateSession(home, thread, InteractiveAgentKind.Codex, usage: false) is { } path
            ? Parse(path, InteractiveAgentKind.Codex, "atf-corr:" + correlation) : null;

    internal static InteractiveTranscript? ReadClaudeSession(string home, string sessionId, string correlation) =>
        LocateSession(home, sessionId, InteractiveAgentKind.Claude, usage: false) is { } path
            ? Parse(path, InteractiveAgentKind.Claude, "atf-corr:" + correlation) : null;
    public IReadOnlySet<string>? LiveSessions(InteractiveLaunch launch, int shellPid) =>
        LiveNativeSessions.Read(launch.Kind, shellPid,
            launch.Kind == InteractiveAgentKind.Claude ? ClaudeConfigRoot.Resolve(environment, Path.GetFullPath(launch.WorkingDirectory)) : null);

    public InteractiveTranscript? ReadLatestTurn(InteractiveLaunch launch, IReadOnlyCollection<string> correlations)
    {
        if (launch.NativeTranscript is not { } bound) { return null; }
        try
        {
            if (new FileInfo(bound.Path).Length > MaxTranscriptBytes || HeaderId(bound.Path, launch.Kind) != bound.SessionId) { return null; }
            // One read: the newest ATF turn is the one whose marker appears last; Parse then shows what followed it.
            var lines = LiveFiles.ReadLines(bound.Path).ToList();
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                if (LastMarker(lines[i], correlations) is { } marker) { return Parse(bound.Path, launch.Kind, marker, lines); }
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static string? LastMarker(string line, IReadOnlyCollection<string> correlations)
    {
        if (!line.Contains("atf-corr:", StringComparison.Ordinal)) { return null; }
        return correlations.Select(c => "atf-corr:" + c).Where(m => line.Contains(m, StringComparison.Ordinal))
            .OrderByDescending(m => line.LastIndexOf(m, StringComparison.Ordinal)).FirstOrDefault();
    }

    public NativeTranscriptBinding? Locate(InteractiveLaunch launch, string sessionId)
    {
        if (launch.Kind == InteractiveAgentKind.Pi) { return null; }
        var home = launch.Kind == InteractiveAgentKind.Claude
            ? ClaudeConfigRoot.Resolve(environment, Path.GetFullPath(launch.WorkingDirectory)) : _codexHome;
        return LocateSession(home, sessionId, launch.Kind, usage: false) is { } path ? new(sessionId, path) : null;
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
    static bool? IsParent(string path, InteractiveAgentKind kind, bool usage = false)
    {
        try
        {
            // Codex/Pi carry ancestry in their header; Claude records it on every chain record
            // (system/attachment/message), which can follow a long preamble of metadata lines.
            var lines = LiveFiles.ReadLines(path);
            if (usage) { lines = lines.Take(kind == InteractiveAgentKind.Claude ? 50 : 10); }
            foreach (var line in kind == InteractiveAgentKind.Claude ? lines : lines.Take(10))
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (kind == InteractiveAgentKind.Codex && Str(root, "type") == "session_meta"
                    && root.TryGetProperty("payload", out var meta))
                {
                    if (Str(meta, "source") is { } sourceKind && (usage || sourceKind == "cli")) { return true; }
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
            var projects = Path.Combine(ClaudeConfigRoot.Resolve(environment, cwd), "projects");
            return ClaudeProjectDirectories(projects, cwd).SelectMany(dir => Directory.EnumerateFiles(dir, "*.jsonl"));
        }
        var sessions = Path.Combine(_codexHome, "sessions");
        return Directory.Exists(sessions) ? Directory.EnumerateFiles(sessions, "rollout-*.jsonl", SearchOption.AllDirectories) : [];
    }

    const int ClaudeSlugLimit = 200;

    /// <summary>
    /// Claude's project directories for a cwd. Claude names them after its process cwd, which is
    /// the physical path on Linux/macOS (getcwd: /tmp/x is /private/tmp/x) but the path as given on
    /// Windows (junctions stay unresolved), so both spellings are tried. Every non-alphanumeric UTF-16
    /// unit becomes '-'; a slug over 200 characters is cut to 200 plus '-' and a hash, which Claude
    /// itself matches by that prefix. Real entries are returned so a case-insensitive file system
    /// never yields the same directory twice.
    /// </summary>
    internal static List<string> ClaudeProjectDirectories(string projects, string cwd)
    {
        string? physical = null;
        try { physical = PhysicalPath.Resolve(cwd); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var slugs = new[] { physical, cwd }.OfType<string>()
            .Select(path => new string([.. Path.TrimEndingDirectorySeparator(path).Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]))
            .Distinct().ToList();
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        try
        {
            return Directory.Exists(projects)
                ? [.. Directory.EnumerateDirectories(projects).Where(dir => slugs.Any(slug => Path.GetFileName(dir) is var name
                    && (slug.Length <= ClaudeSlugLimit ? string.Equals(name, slug, comparison)
                        : name.StartsWith(slug[..ClaudeSlugLimit] + "-", comparison))))]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
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

    static InteractiveTranscript? Parse(string path, InteractiveAgentKind kind, string marker, IReadOnlyList<string>? lines = null)
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
            var incomplete = false;
            string? last = null;
            InteractiveApiError? apiError = null;
            DateTimeOffset? rateLimitReset = null;
            var progress = new List<string>();
            var times = new List<string?>();
            var backgroundTools = new HashSet<string>();
            var backgroundTasks = new HashSet<string>();
            var knownTasks = new HashSet<string>();
            foreach (var line in lines ?? LiveFiles.ReadLines(path))
            {
                JsonDocument json;
                try { json = JsonDocument.Parse(line); }
                catch (JsonException)
                {
                    // Unreadable (e.g. partially flushed): it could be the next turn's boundary,
                    // so nothing after it is attributed to this turn.
                    if (markerSeen)
                    {
                        incomplete = true;
                        break;
                    }
                    continue;
                }
                using (json)
                {
                    var root = json.RootElement;
                    var userText = UserText(root, kind) ?? (kind == InteractiveAgentKind.Claude ? ClaudeInboxText(root) : null);
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
                    // Claude folds a notification that arrives mid-turn into the running turn as a
                    // queued_command attachment instead of a user record.
                    if (kind == InteractiveAgentKind.Claude && QueuedCommand(root) is { } queued
                        && TaskNotification(queued, backgroundTasks, knownTasks, out var drainedPending))
                    {
                        if (drainedPending) { completed = false; apiError = null; }
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
                        ended = true;
                        break;
                    }
                    if (kind == InteractiveAgentKind.Claude)
                    {
                        if (Str(root, "type") == "rate_limit_event"
                            && root.TryGetProperty("rate_limit_info", out var limit)
                            && limit.ValueKind == JsonValueKind.Object
                            && Str(limit, "status") == "rejected"
                            && limit.TryGetProperty("resetsAt", out var resetsAt)
                            && resetsAt.ValueKind == JsonValueKind.Number
                            && resetsAt.TryGetInt64(out var epoch))
                        {
                            try { rateLimitReset = DateTimeOffset.FromUnixTimeSeconds(epoch); }
                            catch (ArgumentOutOfRangeException) { }
                        }
                        TrackBackgroundTasks(root, backgroundTools, backgroundTasks, knownTasks);
                        if (Str(root, "type") == "assistant")
                        {
                            completed = CompletedTurn(root, kind) && backgroundTools.Count == 0 && backgroundTasks.Count == 0;
                            apiError = ApiError(root, rateLimitReset);
                        }
                        else if (apiError is not null && Str(root, "type") == "system" && Str(root, "subtype") == "turn_duration")
                        {
                            apiError = apiError with { TurnEnded = true };
                        }
                    }
                    else
                    {
                        completed |= CompletedTurn(root, kind);
                        if (kind == InteractiveAgentKind.Codex && (CodexLoginError(root) ?? CodexTurnError(root)) is { } turnError) { apiError = turnError; }
                    }
                    if (AssistantText(root, kind) is { } text)
                    {
                        last = text;
                        progress.Add(text);
                        times.Add(Str(root, "timestamp"));
                    }
                }
            }
            return markerSeen ? new(id, last is { Length: > MaxResultChars } ? last[^MaxResultChars..] : last, progress, completed,
                !ended && (backgroundTools.Count > 0 || backgroundTasks.Count > 0),
                ApiError: backgroundTools.Count == 0 && backgroundTasks.Count == 0 ? apiError : null, Times: times, Superseded: ended, Incomplete: incomplete) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            WarnUnreadable(path, e);
            return null;
        }
    }

    static InteractiveApiError? ApiError(JsonElement root, DateTimeOffset? reportedReset)
    {
        if (!Flag(root, "isApiErrorMessage") || !root.TryGetProperty("message", out var message)) { return null; }
        var error = Str(root, "error") ?? Str(message, "error");
        var text = AssistantText(root, InteractiveAgentKind.Claude);
        if (text is { Length: > MaxResultChars }) { text = text[^MaxResultChars..]; }
        var login = error is "authentication_failed" or "authentication_error" or "not_logged_in" or "unauthorized" or "invalid_api_key"
            || text?.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) == true;
        var limited = error is "rate_limit" or "rate_limit_error" or "rate_limit_exceeded"
            || text?.Contains("monthly spend limit", StringComparison.OrdinalIgnoreCase) == true
            || text?.Contains("session limit", StringComparison.OrdinalIgnoreCase) == true;
        if (limited)
        {
            var observed = DateTimeOffset.TryParse(Str(root, "timestamp"), out var timestamp) ? timestamp : DateTimeOffset.UtcNow;
            var reset = reportedReset ?? LocalReset(text, observed);
            return new("agent_rate_limited", "usage limit reached"
                + (reset is { } at ? "; resets at " + at.ToUniversalTime().ToString("O") : "; reset time unknown"));
        }
        return login
            ? new("agent_login_required", "Claude is not logged in; run `claude` and /login."
                + (string.IsNullOrWhiteSpace(text) ? "" : " " + text))
            : new("agent_api_error", string.IsNullOrWhiteSpace(text) ? $"Claude API error: {error ?? "unknown"}" : text);
    }

    /// <summary>
    /// A turn Codex ended on a definite 401 (an expired or revoked login): the error its task_complete
    /// (or a final error event) records, with the structured HTTP status when Codex gives one.
    /// Retries ("Reconnecting...") are not final, and other failures are not a login problem.
    /// </summary>
    static InteractiveApiError? CodexLoginError(JsonElement root)
    {
        if (Str(root, "type") != "event_msg" || !root.TryGetProperty("payload", out var payload)) { return null; }
        var (message, info) = Str(payload, "type") switch
        {
            "task_complete" when payload.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                => (Str(error, "message"), error.TryGetProperty("codex_error_info", out var details) ? details : default),
            "error" => (Str(payload, "message"), payload.TryGetProperty("codex_error_info", out var details) ? details : default),
            _ => (null, default),
        };
        if (message is null || message.StartsWith("Reconnecting", StringComparison.Ordinal)
            || !message.Contains("401 Unauthorized", StringComparison.Ordinal)) { return null; }
        // The text alone suffices only when Codex gives no structured status to contradict it.
        if (info.ValueKind == JsonValueKind.Object && HttpStatus(info) is { } status && status != 401) { return null; }
        return BackendLoginErrors.Inspect("codex", message) is { } login ? new(login.Code, login.Details, TurnEnded: true) : null;
    }

    /// <summary>
    /// Codex ended the turn itself on an error (e.g. server_overloaded "Selected model is at capacity").
    /// Retries are separate `error` events, so only task_complete is final.
    /// </summary>
    static InteractiveApiError? CodexTurnError(JsonElement root) =>
        Str(root, "type") == "event_msg" && root.TryGetProperty("payload", out var payload) && Str(payload, "type") == "task_complete"
            && payload.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
            ? new("agent_api_error", "Codex: " + (Str(error, "message") ?? "turn failed"), TurnEnded: true) : null;

    static int? HttpStatus(JsonElement info)
    {
        foreach (var variant in info.EnumerateObject())
        {
            if (variant.Value.ValueKind == JsonValueKind.Object && variant.Value.TryGetProperty("http_status_code", out var code)
                && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var status))
            {
                return status;
            }
        }
        return null;
    }

    static DateTimeOffset? LocalReset(string? text, DateTimeOffset observed)
    {
        var match = Regex.Match(text ?? "", @"\bresets?\s+(?:at\s+)?(?<hour>\d{1,2})(?::(?<minute>\d\d))?\s*(?<meridiem>am|pm)\s*\((?<zone>[^)]+)\)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) { return null; }
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(match.Groups["zone"].Value);
            var local = TimeZoneInfo.ConvertTime(observed, zone);
            var hour = int.Parse(match.Groups["hour"].Value) % 12
                + (match.Groups["meridiem"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
            var reset = new DateTime(local.Year, local.Month, local.Day, hour,
                match.Groups["minute"].Success ? int.Parse(match.Groups["minute"].Value) : 0, 0, DateTimeKind.Unspecified);
            if (TimeZoneInfo.ConvertTimeToUtc(reset, zone) <= observed.UtcDateTime) { reset = reset.AddDays(1); }
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(reset, zone), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return null;
        }
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
        var text = string.Concat(content.EnumerateArray().Where(item => Str(item, "type") == wanted).Select(item => Str(item, "text")));
        // Codex injects context (e.g. a mid-turn date change) as user-role items; that is not user input.
        if (kind == InteractiveAgentKind.Codex && (text.TrimStart().StartsWith("<environment_context>", StringComparison.Ordinal) || NotUserKinds(message)))
        {
            return null;
        }
        return text;
    }

    static bool NotUserKinds(JsonElement message) =>
        message.TryGetProperty("internal_chat_message_metadata_passthrough", out var meta)
        && meta.ValueKind == JsonValueKind.Object
        && meta.TryGetProperty("content_item_kinds", out var kinds)
        && kinds.ValueKind == JsonValueKind.Array
        && !kinds.EnumerateArray().Any(k => k.ValueKind == JsonValueKind.String && k.GetString()!.StartsWith("user.", StringComparison.Ordinal));

    // Claude's authenticated inbox persists the posted user line as isMeta=true.
    // Only this native channel shape is a turn boundary; other meta rows remain metadata.
    static string? ClaudeInboxText(JsonElement root)
    {
        if (Str(root, "type") != "user" || !Flag(root, "isMeta")
            || !root.TryGetProperty("message", out var message) || Str(message, "role") != "user") { return null; }
        var content = Str(message, "content");
        return content?.StartsWith("Another Claude session sent a message:\n", StringComparison.Ordinal) == true
            ? content : null;
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

    static string? QueuedCommand(JsonElement root) =>
        Str(root, "type") == "attachment" && root.TryGetProperty("attachment", out var attachment)
            && Str(attachment, "type") == "queued_command" ? Str(attachment, "prompt") : null;

    static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
