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
    static readonly string[] Backends = ["codex", "pi"];
    readonly string _path;
    readonly Func<string, IReadOnlyCollection<string>> _catalog;
    readonly Lock _gate = new();
    readonly List<TierOverride> _overrides;

    public TierMap(string statePath, Func<string, IReadOnlyCollection<string>> catalog)
    {
        _path = Path.Combine(statePath, "tier-map.json");
        _catalog = catalog;
        _overrides = File.Exists(_path)
            ? JsonSerializer.Deserialize(File.ReadAllText(_path), TierMapJson.Default.ListTierOverride) ?? [] : [];
        if (_overrides.Any(row => !ValidNames(row.Backend, row.Tier) || !ValidEffort(row.Backend, row.Effort)
            || !AcceptJob.ValidOption(row.Model)))
        {
            throw new InvalidDataException("Invalid tier-map.json");
        }
    }

    public (string Model, string Effort) Effective(string backend, string tier)
    {
        lock (_gate)
        {
            var row = _overrides.Find(item => item.Backend == backend && item.Tier == tier);
            return row is null ? ModelSelection.DefaultTier(backend, tier) : (row.Model, row.Effort);
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
                return new TierSetting(backend, tier, row?.Model ?? defaultModel, row?.Effort ?? defaultEffort,
                    defaultModel, defaultEffort, row is not null);
            }))];
        }
    }

    public static IReadOnlyList<string> Efforts(string backend) => backend == "pi"
        ? ["off", "minimal", "low", "medium", "high", "xhigh", "max"]
        : backend == "codex" ? ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"] : [];

    static bool ValidNames(string backend, string tier) => ModelSelection.TierNames(backend).Contains(tier, StringComparer.Ordinal);
    static bool ValidEffort(string backend, string effort) => backend == "pi"
        ? PiThinking.Valid(effort) : backend == "codex" && Efforts(backend).Contains(effort);

    public void Change(string? backend, string? tier, string? model, string? effort, bool resetAll = false)
    {
        if (!resetAll && (backend is null || tier is null || !ValidNames(backend, tier)))
        {
            throw new ArgumentException("Unknown backend or tier");
        }

        if (!resetAll && model is not null)
        {
            if (effort is null || !ValidEffort(backend!, effort) || !AcceptJob.ValidOption(model))
            {
                throw new ArgumentException("Invalid model or effort");
            }
            var known = _catalog(backend!);
            if (known.Count > 0 && !(backend == "pi"
                ? known.Any(candidate => candidate.Split('/', 2)[^1] == model.Split('/', 2)[^1])
                : known.Contains(model)))
            {
                var hint = backend == "codex" ? "npm install -g @openai/codex@latest"
                    : "npm install -g @earendil-works/pi-coding-agent@latest";
                throw new ArgumentException($"Model '{model}' is not available for {backend} on this machine. Upgrade {backend}: {hint}");
            }
        }
        lock (_gate)
        {
            var next = resetAll ? [] : _overrides.Where(row => row.Backend != backend || row.Tier != tier).ToList();
            if (!resetAll && model is not null)
            {
                var (defaultModel, defaultEffort) = ModelSelection.DefaultTier(backend!, tier!);
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
