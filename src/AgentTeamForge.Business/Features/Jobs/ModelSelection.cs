namespace AgentTeamForge.Business.Features.Jobs;

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

    static readonly BackendModelDiscovery DefaultDiscovery = new();

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
        var available = (discover ?? DefaultDiscovery.GetModels)(backend);
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

}
