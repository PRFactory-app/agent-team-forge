using System.Text.Json;
using System.Text.Json.Serialization;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Jobs;

public sealed record TierOverride(string Backend, string Tier, string Model, string Effort);
public sealed record TierSetting(string Backend, string Tier, string Model, string Effort,
    string DefaultModel, string DefaultEffort, bool Custom);

/// <summary>Private, per-state-directory capability tier overrides.</summary>
public sealed class TierMap
{
    static readonly string[] Backends = ["codex", "pi", "cursor", "droid"];
    readonly string _path;
    readonly Func<string, IReadOnlyCollection<string>> _catalog;
    readonly Func<string, IReadOnlyCollection<string>> _lookup;
    readonly Lock _gate = new();
    readonly List<TierOverride> _overrides;

    public TierMap(string statePath, Func<string, IReadOnlyCollection<string>> catalog, Action<string>? log = null,
        Func<string, IReadOnlyCollection<string>>? lookup = null)
    {
        _path = Path.Combine(statePath, "tier-map.json");
        _catalog = catalog;
        _lookup = lookup ?? catalog;
        _overrides = Load(_path, log);
    }

    // A missing, unreadable or corrupt file must not stop the daemon: fall back to the built-in defaults.
    static List<TierOverride> Load(string path, Action<string>? log)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var rows = JsonSerializer.Deserialize(File.ReadAllText(path), TierMapJson.Default.ListTierOverride) ?? [];
            if (rows.All(row => row is not null && row.Backend is not null && row.Tier is not null && row.Model is not null
                && row.Effort is not null && ValidNames(row.Backend, row.Tier) && ValidEffort(row.Backend, row.Effort)
                && AcceptJob.ValidOption(row.Model)))
            {
                return rows;
            }
            log?.Invoke($"warning: ignoring invalid {path}; using default capability tiers");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"warning: ignoring unreadable {path} ({ex.GetType().Name}); using default capability tiers");
        }
        return [];
    }

    /// <summary>The explicit user override for a tier, or null when the built-in default applies.</summary>
    public (string Model, string Effort)? Override(string backend, string tier)
    {
        lock (_gate)
        {
            var row = _overrides.Find(item => item.Backend == backend && item.Tier == tier);
            return row is null ? null : (row.Model, row.Effort);
        }
    }

    public IReadOnlyList<TierSetting> Settings()
    {
        lock (_gate)
        {
            return [.. Backends.SelectMany(backend => ModelSelection.TierNames(backend).Select(tier =>
            {
                var (defaultModel, defaultEffort) = ModelSelection.DefaultTier(backend, tier);
                var row = _overrides.Find(item => item.Backend == backend && item.Tier == tier);
                var (activeModel, _) = ModelSelection.DefaultTier(backend, tier, _catalog(backend));
                return new TierSetting(backend, tier, row?.Model ?? activeModel, row?.Effort ?? defaultEffort,
                    defaultModel, defaultEffort, row is not null);
            }))];
        }
    }

    public static IReadOnlyList<string> Efforts(string backend) => backend == "pi"
        ? ["off", "minimal", "low", "medium", "high", "xhigh", "max"]
        : backend == "codex" ? ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"]
        : backend == "droid" ? ["none", "dynamic", "off", "minimal", "low", "medium", "high", "xhigh", "max"]
        : backend == "cursor" ? ["none"] : [];

    static bool ValidNames(string backend, string tier) => ModelSelection.TierNames(backend).Contains(tier, StringComparer.Ordinal);
    static bool ValidEffort(string backend, string effort) => backend == "pi"
        ? PiThinking.Valid(effort) : Efforts(backend).Contains(effort);

    public void Change(string? backend, string? tier, string? model, string? effort, bool resetAll = false)
    {
        if (!resetAll && (backend is null || tier is null || !ValidNames(backend, tier)))
        {
            throw new ArgumentException("Unknown backend or tier");
        }

        var defaultModel = "";
        var defaultEffort = "";
        if (!resetAll && model is not null)
        {
            if (effort is null || !ValidEffort(backend!, effort) || !AcceptJob.ValidOption(model))
            {
                throw new ArgumentException("Invalid model or effort");
            }
            // Capture what the row showed before the fresh lookup fills the cache; only a cold cache needs the (blocking) lookup.
            var cached = _catalog(backend!);
            var shown = ModelSelection.DefaultTier(backend!, tier!, cached).Model;
            var known = cached.Count > 0 ? cached : _lookup(backend!);
            (defaultModel, defaultEffort) = ModelSelection.DefaultTier(backend!, tier!, known);
            // The operator kept the (stale) model the row showed: follow the effective default instead of pinning the fallback.
            if (model == shown) { model = defaultModel; }
            if (known.Count > 0 && !(backend == "cursor" && model == "auto") && !(backend == "pi"
                ? known.Any(candidate => candidate.Split('/', 2)[^1] == model.Split('/', 2)[^1])
                : known.Contains(model)))
            {
                var hint = backend == "codex" ? "npm install -g @openai/codex@latest"
                    : backend == "cursor" ? "run cursor-agent --list-models or check account access"
                    : "npm install -g @earendil-works/pi-coding-agent@latest";
                throw new ArgumentException($"Model '{model}' is not available for {backend} on this machine. Upgrade {backend}: {hint}");
            }
        }
        lock (_gate)
        {
            var next = resetAll ? [] : _overrides.Where(row => row.Backend != backend || row.Tier != tier).ToList();
            if (!resetAll && model is not null)
            {
                if (model != defaultModel || effort != defaultEffort)
                {
                    next.Add(new TierOverride(backend!, tier!, model, effort!));
                }
            }
            Write(next);
            _overrides.Clear();
            _overrides.AddRange(next);
        }
    }

    void Write(List<TierOverride> rows)
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                JsonSerializer.Serialize(stream, rows, TierMapJson.Default.ListTierOverride);
                stream.Flush(true);
            }
            File.Move(temp, _path, true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(List<TierOverride>))]
internal sealed partial class TierMapJson : JsonSerializerContext;
