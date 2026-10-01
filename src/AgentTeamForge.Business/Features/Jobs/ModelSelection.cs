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
            ["medium"] = ("gpt-6.1-sol", "low"),
            ["high"] = ("gpt-6.1-sol", "medium"),
            ["xhigh"] = ("gpt-6.1-sol", "high"),
            ["max"] = ("gpt-6-astra", "low"),
        };

    static readonly string[] SharedTierOrder = [.. Tiers.Keys];
    static readonly string[] DroidEfforts = ["none", "low", "medium", "high", "xhigh", "max"];
    public static IReadOnlyList<string> TierNames(string backend) => backend == "pi"
        ? [.. SharedTierOrder.Take(3), "medium-fast", .. SharedTierOrder.Skip(3)]
        : backend is "codex" or "cursor" or "droid" ? SharedTierOrder : [];

    // Use the previous slug only when a known catalog lists it instead of the current model.
    static readonly string[] SolCandidates = ["gpt-6.1-sol", "gpt-6-sol"];

    static bool IsSolTier(string backend, string tier) =>
        backend == "pi" && tier == "medium-fast" || backend is "codex" or "pi" && tier is "medium" or "high" or "xhigh";

    public static (string Model, string Effort) DefaultTier(string backend, string tier) =>
        backend == "pi" && tier == "medium-fast" ? ("gpt-6.1-sol", "medium")
        : backend == "cursor" && SharedTierOrder.Contains(tier) ? ("auto", "none")
        : backend == "droid" && Array.IndexOf(SharedTierOrder, tier) is var index and >= 0
            ? ("claude-opus-5", DroidEfforts[index])
        : backend is "codex" or "pi" && Tiers.TryGetValue(tier, out var value) ? value
        : throw new ArgumentException("Unknown backend or tier");

    /// <summary>Built-in default for a tier: prefer the current model when the catalog is unknown.</summary>
    public static (string Model, string Effort) DefaultTier(string backend, string tier, IReadOnlyCollection<string> available)
    {
        var (model, effort) = DefaultTier(backend, tier);
        if (!IsSolTier(backend, tier))
        {
            return (model, effort);
        }

        if (available.Count == 0)
        {
            return (model, effort);
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

    // The levels each CLI's help lists; codex narrows them per model when discovery reports levels.
    static readonly string[] ClaudeEfforts = ["low", "medium", "high", "xhigh", "max"];
    static readonly string[] CodexEfforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"];
    static readonly string[] PiEfforts = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];
    static readonly string[] DroidSettingEfforts = ["none", "dynamic", "off", "minimal", "low", "medium", "high", "xhigh", "max"];
    static readonly string[] CursorEfforts = ["none"];

    /// <summary>The one list of effort values a backend accepts when its catalog has no per-model levels.</summary>
    public static IReadOnlyList<string> Efforts(string backend) => backend switch
    {
        "claude" => ClaudeEfforts,
        "codex" => CodexEfforts,
        "pi" => PiEfforts,
        "droid" => DroidSettingEfforts,
        "cursor" => CursorEfforts,
        _ => [],
    };

    /// <summary>Effort values accepted for <paramref name="model"/>: its discovered levels, else the backend list.</summary>
    public static IReadOnlyList<string> Efforts(string backend, string? model, IReadOnlyCollection<string> catalog) =>
        model is not null && catalog is DiscoveredModelCollection known && known.EffortsFor(model) is { } reported ? reported : Efforts(backend);

    /// <summary>Null when the backend accepts <paramref name="effort"/> for <paramref name="model"/>; otherwise the reason.</summary>
    public static string? EffortError(string backend, string? model, string effort, IReadOnlyCollection<string> catalog)
    {
        var allowed = Efforts(backend, model, catalog);
        if (allowed.Count == 0 || allowed.Contains(effort, StringComparer.Ordinal))
        {
            return null;
        }

        var target = model is null ? backend : $"{backend} model '{model}'";
        return $"Unsupported effort '{effort}' for {target}. Supported: {string.Join(", ", allowed)}";
    }

    static string? Checked(string backend, string? model, string? effort, IReadOnlyCollection<string> catalog) =>
        effort is not null && EffortError(backend, model, effort, catalog) is { } error ? throw new ArgumentException(error) : effort;

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
        ConsoleSelectionError(backend, model, effort) is null;

    /// <summary>Null for a choice the console offers; otherwise the field at fault and why.</summary>
    public static (string Field, string Message)? ConsoleSelectionError(string backend, string? model, string? effort)
    {
        if (!ConsoleOptions.TryGetValue(backend, out var options))
        {
            return ("backend", $"Unknown backend '{backend}'. Supported: {string.Join(", ", ConsoleOptions.Keys)}");
        }
        if (model is null || !options.Models.Contains(model, StringComparer.Ordinal))
        {
            return ("model", $"Unsupported model '{model}' for {backend}. Supported: {string.Join(", ", options.Models)}");
        }
        if (effort is null || options.Efforts.Contains(effort, StringComparer.Ordinal))
        {
            return null;
        }
        return ("effort", options.Efforts.Count == 0
            ? $"The {backend} tier sets the effort; leave effort empty or change the tier in Settings."
            : $"Unsupported effort '{effort}' for {backend}. Supported: {string.Join(", ", options.Efforts)}");
    }

    static readonly BackendModelDiscovery DefaultDiscovery = new();

    public static (string? Model, string? Effort) Resolve(string backend, string? model, string? effort,
        Func<string, IReadOnlyCollection<string>>? discover = null, TierMap? tierMap = null)
    {
        var key = model?.Trim();
        if (backend == "claude")
        {
            var claudeModel = string.IsNullOrEmpty(key) ? "opus"
                : ClaudeModelMap.GetValueOrDefault(key)
                    ?? throw new ArgumentException($"Unsupported model '{key}' for claude-code. Supported: {string.Join(", ", ClaudeModels)}");
            return (claudeModel, Checked(backend, null, effort, []));
        }

        // Droid also takes "ultra", which its backend passes as max.
        if (backend == "droid" && effort != "ultra")
        {
            Checked(backend, null, effort, []);
        }
        if (backend == "cursor" && string.IsNullOrEmpty(key)) { return (key, null); }

        if (backend is not ("codex" or "pi" or "cursor" or "droid") || string.IsNullOrEmpty(key))
        {
            return (key, backend is "codex" or "pi" ? Checked(backend, null, effort, []) : effort);
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
                return (null, Checked(backend, null, effort, [])); // Reference pi raw-slug soft fallback.
            }

            var hint = backend == "codex" ? "npm install -g @openai/codex@latest"
                : backend == "cursor" ? "run cursor-agent --list-models or check account access"
                : "npm install -g @earendil-works/pi-coding-agent@latest (GPT-6 Sol/Luna need pi >= 0.87.1; or add the model to your provider config)";
            throw new ArgumentException($"Model '{selected}' is not available for {backend} on this machine. Available: {string.Join(", ", available)}. Upgrade the CLI or check account access. Upgrade {backend}: {hint}");
        }

        return tier is { } resolved ? (resolved.Model, backend == "cursor" ? null : resolved.Effort)
            : (key, backend == "cursor" ? null : backend == "droid" ? effort : Checked(backend, key, effort, available));
    }

}
