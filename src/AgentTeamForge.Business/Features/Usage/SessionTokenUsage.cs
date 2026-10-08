using System.Text.Json;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;

namespace AgentTeamForge.Business.Features.Usage;

/// <summary>Session token figures; a null member was not reported by the backend and is unknown, never zero.</summary>
public sealed record TokenUsage(long? Input, long? Output, long? CacheRead, long? CacheWrite)
{
    /// <summary>Sum of the known figures (a lower bound when any figure is unknown).</summary>
    public long Total => (Input ?? 0) + (Output ?? 0) + (CacheRead ?? 0) + (CacheWrite ?? 0);

    /// <summary>Adds two reports; a figure stays known only when both carry it.</summary>
    public static TokenUsage operator +(TokenUsage a, TokenUsage b)
    {
        return new(a.Input + b.Input, a.Output + b.Output, a.CacheRead + b.CacheRead, a.CacheWrite + b.CacheWrite);
    }
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
                if (Resolve(ref kind, sessionId, ref home, cwd) is not { } agent) { return null; }
                var key = (kind, sessionId, home!);
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
                    entry.Path = InteractiveTranscriptReader.LocateSession(home!, sessionId, agent);
                    if (entry.Path is null) { return entry.Usage; }
                }
                ReadChunk(entry, kind, final: false);
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

    /// <summary>Whole-transcript usage for a finished session: no retry gate, no per-call byte cap, nothing cached.</summary>
    public static TokenUsage? ReadFinal(string kind, string sessionId, string? home, string? cwd = null)
    {
        try
        {
            if (Resolve(ref kind, sessionId, ref home, cwd) is not { } agent) { return null; }
            var entry = new Entry { Path = InteractiveTranscriptReader.LocateSession(home!, sessionId, agent) };
            if (entry.Path is null) { return null; }
            while (ReadChunk(entry, kind, final: true) > 0) { }
            return entry.Usage;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    static InteractiveAgentKind? Resolve(ref string kind, string sessionId, ref string? home, string? cwd)
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
        return kind switch
        {
            "claude" => InteractiveAgentKind.Claude,
            "codex" => InteractiveAgentKind.Codex,
            _ => InteractiveAgentKind.Pi
        };
    }

    /// <summary>Parses at most <see cref="MaxBytes"/> from the entry's offset and returns the bytes consumed.
    /// A final read also parses an unterminated last line at end of file.</summary>
    static int ReadChunk(Entry entry, string kind, bool final)
    {
        using var stream = new FileStream(entry.Path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < entry.Offset)
        {
            entry.Offset = 0;
            entry.Usage = null;
            entry.Seen.Clear();
            entry.SkippingLine = false;
        }
        var count = (int)Math.Min(MaxBytes, stream.Length - entry.Offset);
        if (count == 0) { return 0; }
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
        if (final && start < read && entry.Offset + read >= stream.Length)
        {
            if (!entry.SkippingLine) { Parse(bytes.AsMemory(start, read - start), kind, entry); }
            entry.SkippingLine = false;
            start = read;
        }
        // Oversized lines are skipped in bounded chunks; normal partial tails wait.
        else if (start == 0 && read == MaxBytes) { entry.SkippingLine = true; start = read; }
        entry.Offset += start;
        return start;
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
                // Codex reports no cache-write figure; it stays unknown.
                var input = Number(usage, "input_tokens");
                var cached = Number(usage, "cached_input_tokens");
                entry.Usage = new(input is null || cached is null ? null : Math.Max(0, input.Value - cached.Value),
                    Number(usage, "output_tokens"), cached, null);
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
            entry.Usage = (entry.Usage ?? new TokenUsage(0, 0, 0, 0)) + value;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException) { }
    }

    static string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    static long? Number(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n) ? Math.Max(0, n) : null;
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
