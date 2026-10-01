using System.Diagnostics;
using AgentTeamForge.Business.Features.Agents.Backends;
using System.Text.Json;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>
/// Bounded, single-flight CLI catalog lookup per backend. A catalog is reused
/// for a few minutes so a CLI upgrade is noticed without a daemon restart; an
/// unknown (failed or empty) catalog is retried sooner.
/// </summary>
public sealed class BackendModelDiscovery
{
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    static readonly TimeSpan DefaultCatalogTtl = TimeSpan.FromMinutes(5);
    static readonly TimeSpan DefaultUnknownTtl = TimeSpan.FromMinutes(1);
    readonly Dictionary<string, (Lazy<IReadOnlyCollection<string>> Lookup, long Started)> _cache = [];
    readonly Dictionary<string, IReadOnlyCollection<string>> _known = [];
    readonly TimeSpan _timeout;
    readonly TimeSpan _catalogTtl;
    readonly TimeSpan _unknownTtl;
    readonly Func<string, string> _binary;

    public BackendModelDiscovery() : this(DefaultTimeout, backend => backend) { }

    internal BackendModelDiscovery(TimeSpan timeout, Func<string, string> binary, TimeSpan? catalogTtl = null, TimeSpan? unknownTtl = null)
    {
        _timeout = timeout;
        _binary = binary;
        _catalogTtl = catalogTtl ?? DefaultCatalogTtl;
        _unknownTtl = unknownTtl ?? DefaultUnknownTtl;
    }

    public IReadOnlyCollection<string> GetModels(string backend)
    {
        Lazy<IReadOnlyCollection<string>> lookup;
        lock (_cache)
        {
            if (!_cache.TryGetValue(backend, out var entry) || Expired(entry.Lookup, entry.Started))
            {
                entry = (new Lazy<IReadOnlyCollection<string>>(() =>
                {
                    var models = Discover(backend);
                    lock (_cache) { _known[backend] = models; }
                    return models;
                }), Environment.TickCount64);
                _cache[backend] = entry;
            }

            lookup = entry.Lookup;
        }

        // Concurrent callers share the in-flight lookup; nothing is held while it runs.
        return lookup.Value;
    }

    public IReadOnlyCollection<string> CachedModels(string backend)
    {
        // The last completed lookup, so an in-flight refresh does not blank the catalog.
        lock (_cache) { return _known.GetValueOrDefault(backend) ?? []; }
    }

    /// <summary>Start catalog lookups in the background; a failure leaves that catalog unknown.</summary>
    public Task Warm(IEnumerable<string> backends) =>
        Task.WhenAll(backends.Where(backend => backend is "codex" or "pi" or "cursor").Distinct()
            .Select(backend => Task.Run(() => GetModels(backend))));

    bool Expired(Lazy<IReadOnlyCollection<string>> lookup, long started) =>
        lookup.IsValueCreated
        && TimeSpan.FromMilliseconds(Environment.TickCount64 - started) >= (lookup.Value.Count == 0 ? _unknownTtl : _catalogTtl);

    IReadOnlyCollection<string> Discover(string backend)
    {
        if (backend is not ("codex" or "pi" or "cursor"))
        {
            return [];
        }

        using var process = new Process();
        var started = false;
        try
        {
            process.StartInfo = new ProcessStartInfo(_binary(backend == "cursor" ? "cursor-agent" : backend))
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in backend == "codex" ? new[] { "debug", "models" } : ["--list-models"])
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            process.Start();
            started = true;
            process.StandardInput.Close();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(_timeout);
            process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
            var output = outputTask.WaitAsync(deadline.Token).GetAwaiter().GetResult();
            errorTask.WaitAsync(deadline.Token).GetAwaiter().GetResult();
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
            if (backend == "cursor")
            {
                return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim().TrimStart('-', '*', ' '))
                    .Where(line => line.Length > 0 && line.Length < 128 && !line.Contains(' '))];
            }

            return ParseCodex(output);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Missing binary, changed output, or deadline: the catalog is unknown.
            if (started && !process.HasExited)
            {
                try { OwnedProcessTermination.Kill(process); }
                catch (Exception killError) when (killError is not OutOfMemoryException) { }
            }
            return [];
        }
    }

    /// <summary>Listed API models from <c>codex debug models</c>, with each model's reasoning levels when it reports them.</summary>
    internal static IReadOnlyCollection<string> ParseCodex(string output)
    {
        using var document = JsonDocument.Parse(output);
        var models = new List<string>();
        var efforts = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.GetProperty("models").EnumerateArray())
        {
            if (!(item.TryGetProperty("supported_in_api", out var supported) && supported.ValueKind == JsonValueKind.True
                && item.TryGetProperty("visibility", out var visibility) && visibility.GetString() == "list"
                && item.TryGetProperty("slug", out var slug) && slug.GetString() is { Length: > 0 } name))
            {
                continue;
            }

            models.Add(name);
            if (item.TryGetProperty("supported_reasoning_levels", out var levels) && levels.ValueKind == JsonValueKind.Array)
            {
                string[] known = [.. levels.EnumerateArray()
                    .Select(level => level.ValueKind == JsonValueKind.Object && level.TryGetProperty("effort", out var effort) ? effort.GetString() : null)
                    .OfType<string>().Where(effort => effort.Length > 0)];
                if (known.Length > 0) { efforts[name] = known; }
            }
        }
        return new DiscoveredModelCollection(models, efforts);
    }
}

/// <summary>A discovered model list; also knows each model's reasoning levels when the CLI reports them.</summary>
public sealed class DiscoveredModelCollection(IReadOnlyList<string> models, IReadOnlyDictionary<string, IReadOnlyList<string>> efforts) : IReadOnlyCollection<string>
{
    public int Count => models.Count;

    /// <summary>The model's reported levels, or null when the CLI did not report any.</summary>
    public IReadOnlyList<string>? EffortsFor(string model) => efforts.GetValueOrDefault(model);

    public IEnumerator<string> GetEnumerator() => models.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
