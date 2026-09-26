using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace AgentTeamForge.Business.Features.Jobs;

public sealed record AgentModelOptions(IReadOnlyList<string> Models, IReadOnlyList<string> Efforts);

/// <summary>Resolve the caller's model choice once, before the job is accepted.</summary>
public static class ModelSelection
{
    static readonly Dictionary<string, (string Model, string Effort)> Tiers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["cheapest"] = ("gpt-6-luna", "high"),
            ["low"] = ("gpt-6-luna", "xhigh"),
            ["medium"] = ("gpt-6-luna", "max"),
            ["high"] = ("gpt-6-sol", "high"),
            ["xhigh"] = ("gpt-6-astra", "low"),
            ["max"] = ("gpt-6-astra", "medium"),
        };

    static readonly string[] SharedTierOrder = [.. Tiers.Keys];
    static readonly string[] ClaudeModels = ["opus", "sonnet", "haiku", "fable", "fast", "balanced", "powerful"];
    static readonly string[] ClaudeEfforts = ["low", "medium", "high", "xhigh", "max"];

    public static IReadOnlyDictionary<string, AgentModelOptions> ConsoleOptions { get; } =
        new Dictionary<string, AgentModelOptions>
        {
            ["claude"] = new(ClaudeModels, ClaudeEfforts),
            ["codex"] = new(SharedTierOrder, []),
            ["pi"] = new([.. SharedTierOrder.Take(3), "medium-fast", .. SharedTierOrder.Skip(3)], []),
        };

    public static bool ValidConsoleSelection(string backend, string? model, string? effort) =>
        ConsoleOptions.TryGetValue(backend, out var options)
        && model is not null && options.Models.Contains(model, StringComparer.Ordinal)
        && (effort is null || options.Efforts.Contains(effort, StringComparer.Ordinal));

    static readonly ConcurrentDictionary<string, IReadOnlyCollection<string>> Discovered = new();

    public static (string? Model, string? Effort) Resolve(string backend, string? model, string? effort,
        Func<string, IReadOnlyCollection<string>>? discover = null)
    {
        var key = model?.Trim();
        if (backend == "claude")
        {
            return (key switch
            {
                null or "" => "opus",
                "fast" => "haiku",
                "balanced" => "sonnet",
                "powerful" => "opus",
                "haiku" or "sonnet" or "opus" or "fable" => key,
                _ => throw new ArgumentException($"Unsupported model '{key}' for claude-code. Supported: haiku, sonnet, opus, fable"),
            }, effort);
        }

        if (backend is not ("codex" or "pi") || string.IsNullOrEmpty(key))
        {
            return (key, effort);
        }

        if (backend == "pi" && key.Equals("high-fast", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Tier 'high-fast' was removed from pi; use 'high' instead.");
        }

        var tier = Tiers.TryGetValue(key, out var shared) ? shared
            : backend == "pi" && key.Equals("medium-fast", StringComparison.OrdinalIgnoreCase)
                ? ("gpt-6-sol", "medium") : ((string Model, string Effort)?)null;
        var selected = tier?.Model ?? key;
        var available = (discover ?? Discover)(backend);
        var found = available.Count == 0 || (backend == "pi"
            ? available.Any(candidate => candidate.Split('/', 2)[^1] == selected.Split('/', 2)[^1])
            : available.Contains(selected));
        if (!found)
        {
            if (backend == "pi" && tier is null)
            {
                return (null, effort); // Reference pi raw-slug soft fallback.
            }

            var hint = backend == "codex" ? "npm install -g @openai/codex@latest"
                : "npm install -g @earendil-works/pi-coding-agent@latest (GPT-6 Sol/Luna need pi >= 0.87.1; or add the model to your provider config)";
            throw new ArgumentException($"Model '{selected}' is not available for {backend} on this machine. Available: {string.Join(", ", available)}. Upgrade the CLI or check account access. Upgrade {backend}: {hint}");
        }

        return tier is { } resolved ? (resolved.Model, resolved.Effort) : (key, effort);
    }

    static IReadOnlyCollection<string> Discover(string backend) => Discovered.GetOrAdd(backend, DiscoverUncached);

    static IReadOnlyCollection<string> DiscoverUncached(string backend)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo(backend) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in backend == "codex" ? new[] { "debug", "models" } : ["--list-models"])
            {
                process.StartInfo.ArgumentList.Add(arg);
            }
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync(); // Drain so a chatty CLI cannot block on a full pipe.
            if (!process.WaitForExit(20_000))
            {
                process.Kill(entireProcessTree: true);
                return [];
            }
            var output = outputTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                return [];
            }
            if (backend == "pi")
            {
                return [.. output.Split('\n').Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    .Where(parts => parts.Length > 1 && parts[0] is not ("provider" or "No") && parts[1] != "model")
                    .Select(parts => parts[1])];
            }
            using var document = JsonDocument.Parse(output);
            return [.. document.RootElement.GetProperty("models").EnumerateArray()
                .Where(item => item.TryGetProperty("supported_in_api", out var supported) && supported.ValueKind == JsonValueKind.True
                    && item.TryGetProperty("visibility", out var visibility) && visibility.GetString() == "list"
                    && item.TryGetProperty("slug", out var slug) && slug.ValueKind == JsonValueKind.String)
                .Select(item => item.GetProperty("slug").GetString()!)];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return [];
        }
    }
}
