using System.Text.Json;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Small, forgiving display projection of captured agent output.</summary>
public static class JobActivity
{
    public const int MaxTextChars = 500;
    public const int MaxPageSize = 50;

    public static IReadOnlyList<ActivityEntry> Normalize(string backend, string line, string stream = "stdout")
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("=== run ", StringComparison.Ordinal)
            || line is "[stdout]" or "[stderr]")
        {
            return [];
        }
        if (stream == "stderr")
        {
            return [Entry("error", line)];
        }

        JsonDocument document;
        try { document = JsonDocument.Parse(line); }
        catch (JsonException) { return backend is "claude" or "codex" or "pi" or "cursor" or "droid" ? [] : [Entry("assistant_text", line)]; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }
            var type = String(root, "type");
            var ts = String(root, "timestamp") ?? String(root, "ts");
            var entries = new List<ActivityEntry>();
            void Add(string kind, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    entries.Add(Entry(kind, value, ts));
                }
            }

            if (backend is "cursor" or "droid")
            {
                if (type == "result")
                {
                    Add(root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True ? "error" : "result",
                        String(root, "result"));
                }
            }
            else if (backend is not ("claude" or "codex" or "pi"))
            {
                Add("assistant_text", line);
            }
            else if (backend == "claude")
            {
                switch (type)
                {
                    case "assistant" when root.TryGetProperty("message", out var message):
                        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var part in content.EnumerateArray())
                            {
                                if (String(part, "type") == "text")
                                {
                                    Add("assistant_text", String(part, "text"));
                                }
                                else if (String(part, "type") == "tool_use")
                                {
                                    Add("tool_call", String(part, "name"));
                                }
                            }
                        }
                        break;
                    case "user" when root.TryGetProperty("message", out var toolMessage):
                        if (toolMessage.TryGetProperty("content", out var results) && results.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var part in results.EnumerateArray())
                            {
                                if (String(part, "type") == "tool_result")
                                {
                                    Add("tool_result", Content(part));
                                }
                            }
                        }
                        break;
                    case "result":
                        Add(root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True ? "error" : "result",
                            String(root, "result") ?? String(root, "subtype"));
                        break;
                    case "system":
                        Add("status", String(root, "subtype"));
                        break;
                }
            }
            else if (backend == "codex")
            {
                if (root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
                {
                    var itemType = String(item, "type");
                    if (type == "item.started" && itemType is "command_execution" or "mcp_tool_call" or "file_change")
                    {
                        Add("tool_call", String(item, "command") ?? String(item, "name") ?? itemType);
                    }
                    else if (type == "item.completed")
                    {
                        if (itemType == "agent_message")
                        {
                            Add("assistant_text", String(item, "text"));
                        }
                        else if (itemType is "command_execution" or "mcp_tool_call" or "file_change")
                        {
                            Add("tool_result", String(item, "aggregated_output") ?? String(item, "output") ?? String(item, "status") ?? itemType);
                        }
                    }
                }
                else if (type == "turn.completed")
                {
                    Add("result", "turn completed");
                }
                else if (type is "turn.started" or "thread.started")
                {
                    Add("status", type);
                }
                else if (type is "turn.failed" or "error")
                {
                    Add("error", String(root, "message") ?? type);
                }
            }
            else if (backend == "pi")
            {
                if (type == "message_end" && root.TryGetProperty("message", out var message)
                    && String(message, "role") == "assistant")
                {
                    if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in content.EnumerateArray())
                        {
                            if (String(part, "type") == "text")
                            {
                                Add("assistant_text", String(part, "text"));
                            }
                            else if (String(part, "type") == "toolCall")
                            {
                                Add("tool_call", String(part, "name"));
                            }
                        }
                    }
                    if (String(message, "stopReason") is "error" or "aborted")
                    {
                        Add("error", String(message, "errorMessage") ?? "pi error");
                    }
                }
                else if (type is "tool_execution_start" or "tool_execution_end")
                {
                    Add(type.EndsWith("start", StringComparison.Ordinal) ? "tool_call" : "tool_result", String(root, "toolName") ?? type);
                }
                else if (type == "agent_settled")
                {
                    Add("result", "agent settled");
                }
                else if (type == "agent_start")
                {
                    Add("status", type);
                }
            }
            return entries;
        }
    }

    // Log lines carry no capture time; an event without its own timestamp gets none (the console hides it)
    // rather than the time the detail happened to be read.
    static ActivityEntry Entry(string kind, string text, string? ts = null) =>
        new(ts ?? "", kind, text.Length > MaxTextChars ? text[..MaxTextChars] + "…" : text);

    static string? Content(JsonElement part)
    {
        if (!part.TryGetProperty("content", out var content))
        {
            return null;
        }
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }
        if (content.ValueKind == JsonValueKind.Array)
        {
            return string.Join(" ", content.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && String(x, "type") == "text")
                .Select(x => String(x, "text")));
        }
        return null;
    }

    static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}

public sealed record ActivityEntry(string Ts, string Kind, string Text);
public sealed record JobActivityPage(IReadOnlyList<ActivityEntry> Entries, long NextCursor);
