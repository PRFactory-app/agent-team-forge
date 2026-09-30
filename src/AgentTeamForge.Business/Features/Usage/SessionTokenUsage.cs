using System.Text.Json;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;

namespace AgentTeamForge.Business.Features.Usage;

public sealed record TokenUsage(long Input, long Output, long CacheRead, long CacheWrite)
{
    public long Total => Input + Output + CacheRead + CacheWrite;
}

/// <summary>Bounded, best-effort incremental native transcript usage for web polls.</summary>
public sealed class SessionTokenUsage
{
    const int MaxBytes = 8 * 1024 * 1024;
    readonly Dictionary<(string Kind, string Id, string Home), Entry> cache = [];
    readonly System.Threading.Lock gate = new();
    long clock;

    public TokenUsage? Read(string kind, string sessionId, string? home, string? cwd = null)
    {
        lock (gate)
        {
            Entry? entry = null;
            try
            {
                kind = kind == "claude-code" ? "claude" : kind;
                if (kind is not ("claude" or "codex" or "pi") || string.IsNullOrWhiteSpace(sessionId)
                    || sessionId.IndexOfAny(['/', '\\', '*', '?']) >= 0) { return null; }
                home ??= kind switch
                {
                    "claude" => ClaudeConfigRoot.Resolve(Environment.GetEnvironmentVariable, cwd ?? Environment.CurrentDirectory),
                    "codex" => CodexPaths.Home(Environment.GetEnvironmentVariable, cwd ?? Environment.CurrentDirectory),
                    _ => Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR")
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent")
                };
                var key = (kind, sessionId, home);
                if (!cache.TryGetValue(key, out entry))
                {
                    if (cache.Count >= 512) { cache.Remove(cache.MinBy(p => p.Value.LastUsed).Key); }
                    cache[key] = entry = new Entry();
                }
                entry.LastUsed = ++clock;
                if (entry.Path is null)
                {
                    if (DateTime.UtcNow < entry.RetryAfter) { return entry.Usage; }
                    entry.RetryAfter = DateTime.UtcNow.AddSeconds(30);
                    entry.Path = InteractiveTranscriptReader.LocateSession(home, sessionId, kind switch
                    {
                        "claude" => InteractiveAgentKind.Claude,
                        "codex" => InteractiveAgentKind.Codex,
                        _ => InteractiveAgentKind.Pi
                    });
                    if (entry.Path is null) { return entry.Usage; }
                }
                using var stream = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < entry.Offset)
                {
                    entry.Offset = 0;
                    entry.Usage = null;
                    entry.Seen.Clear();
                    entry.SkippingLine = false;
                }
                var count = (int)Math.Min(MaxBytes, stream.Length - entry.Offset);
                if (count == 0) { return entry.Usage; }
                stream.Position = entry.Offset;
                var bytes = new byte[count];
                var read = 0;
                while (read < count)
                {
                    var n = stream.Read(bytes, read, count - read);
                    if (n == 0) { break; }
                    read += n;
                }
                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (bytes[i] != (byte)'\n') { continue; }
                    if (!entry.SkippingLine) { Parse(bytes.AsMemory(start, i - start), kind, entry); }
                    entry.SkippingLine = false;
                    start = i + 1;
                }
                // Oversized lines are skipped in bounded chunks; normal partial tails wait.
                if (start == 0 && read == MaxBytes) { entry.SkippingLine = true; start = read; }
                entry.Offset += start;
                return entry.Usage;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                if (entry is not null)
                {
                    entry.Path = null;
                    entry.Offset = 0;
                    entry.Usage = null;
                    entry.Seen.Clear();
                    entry.SkippingLine = false;
                    entry.RetryAfter = DateTime.MinValue;
                }
                return null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return entry?.Usage; }
        }
    }

    static void Parse(ReadOnlyMemory<byte> line, string kind, Entry entry)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            JsonElement usage;
            TokenUsage value;
            if (kind == "codex")
            {
                if (Text(root, "type") != "event_msg" || !root.TryGetProperty("payload", out var payload)
                    || Text(payload, "type") != "token_count" || !payload.TryGetProperty("info", out var info)
                    || !info.TryGetProperty("total_token_usage", out usage) || usage.ValueKind != JsonValueKind.Object) { return; }
                var cached = Number(usage, "cached_input_tokens");
                entry.Usage = new(Math.Max(0, Number(usage, "input_tokens") - cached), Number(usage, "output_tokens"), cached, 0);
                return;
            }
            if (Text(root, "type") != (kind == "claude" ? "assistant" : "message")
                || !root.TryGetProperty("message", out var message)
                || (kind == "pi" && Text(message, "role") != "assistant")
                || !message.TryGetProperty("usage", out usage) || usage.ValueKind != JsonValueKind.Object) { return; }
            if (kind == "claude")
            {
                var id = Text(message, "id");
                if (id is null) { return; }
                value = new(Number(usage, "input_tokens"), Number(usage, "output_tokens"), Number(usage, "cache_read_input_tokens"), Number(usage, "cache_creation_input_tokens"));
                if (!entry.Seen.Add(id)) { return; }
            }
            else { value = new(Number(usage, "input"), Number(usage, "output"), Number(usage, "cacheRead"), Number(usage, "cacheWrite")); }
            var old = entry.Usage ?? new TokenUsage(0, 0, 0, 0);
            entry.Usage = new(old.Input + value.Input, old.Output + value.Output, old.CacheRead + value.CacheRead, old.CacheWrite + value.CacheWrite);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException) { }
    }

    static string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    static long Number(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n) ? Math.Max(0, n) : 0;
    sealed class Entry
    {
        public string? Path;
        public long Offset;
        public long LastUsed;
        public DateTime RetryAfter;
        public TokenUsage? Usage;
        public bool SkippingLine;
        public HashSet<string> Seen = [];
    }
}
