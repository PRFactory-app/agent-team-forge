namespace AgentTeamForge.Business.Features.Jobs;

public sealed record AgentModelOptions(IReadOnlyList<string> Models, IReadOnlyList<string> Efforts);

/// <summary>Resolve the caller's model choice once, before the job is accepted.</summary>
public static class ModelSelection
{
    // A tier lists its built-in choices, preferred first. A known catalog gets the first choice it lists.
    static readonly string[] SolCandidates = ["gpt-6.2-sol", "gpt-6.1-sol", "gpt-6-sol"];
    static readonly string[] AstraCandidates = ["gpt-6.2-astra", "gpt-6-astra"];
    const string Terra = "gpt-6.1-terra";

    static (string Model, string Effort)[] Each(string[] models, string effort) => [.. models.Select(model => (model, effort))];

    static readonly Dictionary<string, (string Model, string Effort)[]> Tiers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["cheapest"] = [("gpt-6-luna", "low")],
            ["low"] = [("gpt-6-luna", "medium")],
            ["medium"] = Each(SolCandidates, "low"),
            ["high"] = Each(SolCandidates, "medium"),
            ["xhigh"] = Each(SolCandidates, "high"),
            ["max"] = Each(AstraCandidates, "low"),
        };

    // Codex differs from pi in the lower tiers; without Terra in the catalog it keeps the shared choices.
    static readonly Dictionary<string, (string Model, string Effort)[]> CodexTiers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["cheapest"] = [("gpt-6-luna", "medium")],
            ["low"] = [(Terra, "medium"), .. Tiers["low"]],
            ["medium"] = [(Terra, "high"), .. Tiers["medium"]],
        };

    static readonly (string Model, string Effort)[] PiMediumFast = Each(SolCandidates, "medium");

    static readonly string[] SharedTierOrder = [.. Tiers.Keys];
    static readonly string[] DroidEfforts = ["none", "low", "medium", "high", "xhigh", "max"];
    public static IReadOnlyList<string> TierNames(string backend) => backend == "pi"
        ? [.. SharedTierOrder.Take(3), "medium-fast", .. SharedTierOrder.Skip(3)]
        : backend is "codex" or "cursor" or "droid" ? SharedTierOrder : [];

    static (string Model, string Effort)[]? Candidates(string backend, string tier) => backend switch
    {
        "codex" => CodexTiers.GetValueOrDefault(tier) ?? Tiers.GetValueOrDefault(tier),
        "pi" => tier == "medium-fast" ? PiMediumFast : Tiers.GetValueOrDefault(tier),
        _ => null,
    };

    public static (string Model, string Effort) DefaultTier(string backend, string tier) =>
        backend == "cursor" && SharedTierOrder.Contains(tier) ? ("auto", "none")
        : backend == "droid" && Array.IndexOf(SharedTierOrder, tier) is var index and >= 0
            ? ("claude-opus-5", DroidEfforts[index])
        : Candidates(backend, tier) is { } candidates ? candidates[0]
        : throw new ArgumentException("Unknown backend or tier");

    /// <summary>Built-in default for a tier: prefer the current model when the catalog is unknown.</summary>
    public static (string Model, string Effort) DefaultTier(string backend, string tier, IReadOnlyCollection<string> available)
    {
        var (model, effort) = DefaultTier(backend, tier);
        if (available.Count == 0)
        {
            return (model, effort);
        }

        var listed = Candidates(backend, tier)?.FirstOrDefault(candidate => backend == "pi"
            ? PiCatalogModel(candidate.Model, available) is not null
            : available.Contains(candidate.Model));
        if (listed is { Model: not null } choice)
        {
            (model, effort) = choice;
        }
        return (Canonical(backend, model, available), effort);
    }

    /// <summary>
    /// The pi catalog entry for <paramref name="model"/>, or null when the catalog lacks it. A bare name gains
    /// the listing provider (subscription providers first), so a Claude model is not sent to openai-codex.
    /// </summary>
    public static string? PiCatalogModel(string model, IReadOnlyCollection<string> catalog)
    {
        if (catalog.Contains(model))
        {
            return model;
        }

        var name = model.Split('/', 2)[^1];
        var bare = !model.Contains('/');
        var match = catalog.Where(entry => entry.Split('/', 2)[^1] == name && (bare || !entry.Contains('/')))
            .OrderBy(entry => entry.StartsWith("openai-codex/", StringComparison.Ordinal) || entry.StartsWith("anthropic/", StringComparison.Ordinal) ? 0 : 1)
            .FirstOrDefault();
        return match is null ? null : match.Contains('/') ? match : model;
    }

    /// <summary>The name a backend launches: pi takes its catalog's provider/model when the catalog knows it.</summary>
    public static string Canonical(string backend, string model, IReadOnlyCollection<string> catalog) =>
        backend == "pi" ? PiCatalogModel(model, catalog) ?? model : model;

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
            ? PiCatalogModel(selected, available) is not null
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

        return tier is { } resolved ? (Canonical(backend, resolved.Model, available), backend == "cursor" ? null : resolved.Effort)
            : (Canonical(backend, key, available), backend == "cursor" ? null : backend == "droid" ? effort : Checked(backend, key, effort, available));
    }

}
