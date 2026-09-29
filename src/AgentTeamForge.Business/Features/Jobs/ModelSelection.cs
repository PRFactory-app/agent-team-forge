namespace AgentTeamForge.Business.Features.Jobs;

public sealed record AgentModelOptions(IReadOnlyList<string> Models, IReadOnlyList<string> Efforts);

/// <summary>Resolve the caller's model choice once, before the job is accepted.</summary>
public static class ModelSelection
{
    static readonly Dictionary<string, (string Model, string Effort)> Tiers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["cheapest"] = ("gpt-6-luna", "low"),
            ["low"] = ("gpt-6-luna", "medium"),
            ["medium"] = ("gpt-6-luna", "high"),
            ["high"] = ("gpt-6.1-sol", "high"),
            ["xhigh"] = ("gpt-6-astra", "xhigh"),
            ["max"] = ("gpt-6-astra", "max"),
        };

    static readonly string[] SharedTierOrder = [.. Tiers.Keys];
    static readonly string[] DroidEfforts = ["none", "low", "medium", "high", "xhigh", "max"];
    public static IReadOnlyList<string> TierNames(string backend) => backend == "pi"
        ? [.. SharedTierOrder.Take(3), "medium-fast", .. SharedTierOrder.Skip(3)]
        : backend is "codex" or "cursor" or "droid" ? SharedTierOrder : [];

    // Built-in sol tiers fall back to the previous slug until the backend catalog lists the new one.
    static readonly string[] SolCandidates = ["gpt-6.1-sol", "gpt-6-sol"];

    static bool IsSolTier(string backend, string tier) =>
        backend == "pi" && tier == "medium-fast" || backend is "codex" or "pi" && tier == "high";

    public static (string Model, string Effort) DefaultTier(string backend, string tier) =>
        backend == "pi" && tier == "medium-fast" ? ("gpt-6.1-sol", "medium")
        : backend == "cursor" && SharedTierOrder.Contains(tier) ? ("auto", "none")
        : backend == "droid" && Array.IndexOf(SharedTierOrder, tier) is var index and >= 0
            ? ("claude-opus-5", DroidEfforts[index])
        : backend is "codex" or "pi" && Tiers.TryGetValue(tier, out var value) ? value
        : throw new ArgumentException("Unknown backend or tier");

    /// <summary>Built-in default for a tier: the first candidate the catalog lists (the known-good last one if the catalog is unknown).</summary>
    public static (string Model, string Effort) DefaultTier(string backend, string tier, IReadOnlyCollection<string> available)
    {
        var (model, effort) = DefaultTier(backend, tier);
        if (!IsSolTier(backend, tier))
        {
            return (model, effort);
        }

        if (available.Count == 0)
        {
            return (SolCandidates[^1], effort);
        }

        var pick = SolCandidates.FirstOrDefault(candidate => backend == "pi"
            ? available.Any(name => name.Split('/', 2)[^1] == candidate)
            : available.Contains(candidate));
        return (pick ?? model, effort);
    }

    static readonly Dictionary<string, string> ClaudeModelMap = new(StringComparer.Ordinal)
    {
        ["opus"] = "opus",
        ["sonnet"] = "sonnet",
        ["haiku"] = "haiku",
        ["fable"] = "fable",
        ["fast"] = "haiku",
        ["balanced"] = "sonnet",
        ["powerful"] = "opus",
    };
    static readonly string[] ClaudeModels = [.. ClaudeModelMap.Keys];
    static readonly string[] ClaudeEfforts = ["low", "medium", "high", "xhigh", "max"];

    public static IReadOnlyDictionary<string, AgentModelOptions> ConsoleOptions { get; } =
        new Dictionary<string, AgentModelOptions>
        {
            ["claude"] = new(ClaudeModels, ClaudeEfforts),
            ["codex"] = new(SharedTierOrder, []),
            ["pi"] = new([.. SharedTierOrder.Take(3), "medium-fast", .. SharedTierOrder.Skip(3)], []),
            ["cursor"] = new(SharedTierOrder, []),
            ["droid"] = new(SharedTierOrder, []),
        };

    public static bool ValidConsoleSelection(string backend, string? model, string? effort) =>
        ConsoleOptions.TryGetValue(backend, out var options)
        && model is not null && options.Models.Contains(model, StringComparer.Ordinal)
        && (effort is null || options.Efforts.Contains(effort, StringComparer.Ordinal));

    static readonly BackendModelDiscovery DefaultDiscovery = new();

    public static (string? Model, string? Effort) Resolve(string backend, string? model, string? effort,
        Func<string, IReadOnlyCollection<string>>? discover = null, TierMap? tierMap = null)
    {
        var key = model?.Trim();
        if (backend == "claude")
        {
            if (string.IsNullOrEmpty(key))
            {
                return ("opus", effort);
            }

            return ClaudeModelMap.TryGetValue(key, out var claudeModel) ? (claudeModel, effort)
                : throw new ArgumentException($"Unsupported model '{key}' for claude-code. Supported: haiku, sonnet, opus, fable");
        }

        if (backend == "droid" && effort is not null && effort != "ultra" && !TierMap.Efforts("droid").Contains(effort))
        {
            throw new ArgumentException($"Unsupported Droid reasoning effort '{effort}'");
        }
        if (backend == "cursor" && string.IsNullOrEmpty(key)) { return (key, null); }

        if (backend is not ("codex" or "pi" or "cursor" or "droid") || string.IsNullOrEmpty(key))
        {
            return (key, effort);
        }

        if (backend == "pi" && key.Equals("high-fast", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Tier 'high-fast' was removed from pi; use 'high' instead.");
        }

        var available = backend == "droid" ? [] : (discover ?? DefaultDiscovery.GetModels)(backend);
        var tier = TierNames(backend).Contains(key, StringComparer.OrdinalIgnoreCase)
            ? tierMap?.Override(backend, key.ToLowerInvariant()) ?? DefaultTier(backend, key.ToLowerInvariant(), available)
            : ((string Model, string Effort)?)null;
        var selected = tier?.Model ?? key;
        var found = available.Count == 0 || backend == "cursor" && selected == "auto" || (backend == "pi"
            ? available.Any(candidate => candidate.Split('/', 2)[^1] == selected.Split('/', 2)[^1])
            : available.Contains(selected));
        if (!found)
        {
            if (backend == "pi" && tier is null)
            {
                return (null, effort); // Reference pi raw-slug soft fallback.
            }

            var hint = backend == "codex" ? "npm install -g @openai/codex@latest"
                : backend == "cursor" ? "run cursor-agent --list-models or check account access"
                : "npm install -g @earendil-works/pi-coding-agent@latest (GPT-6 Sol/Luna need pi >= 0.87.1; or add the model to your provider config)";
            throw new ArgumentException($"Model '{selected}' is not available for {backend} on this machine. Available: {string.Join(", ", available)}. Upgrade the CLI or check account access. Upgrade {backend}: {hint}");
        }

        return tier is { } resolved ? (resolved.Model, backend == "cursor" ? null : resolved.Effort)
            : (key, backend == "cursor" ? null : effort);
    }

}
