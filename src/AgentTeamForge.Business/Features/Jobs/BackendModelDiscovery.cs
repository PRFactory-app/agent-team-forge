using System.Diagnostics;
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
                entry = (new Lazy<IReadOnlyCollection<string>>(() => Discover(backend)), Environment.TickCount64);
                _cache[backend] = entry;
            }

            lookup = entry.Lookup;
        }

        // Concurrent callers share the in-flight lookup; nothing is held while it runs.
        return lookup.Value;
    }

    bool Expired(Lazy<IReadOnlyCollection<string>> lookup, long started) =>
        lookup.IsValueCreated
        && TimeSpan.FromMilliseconds(Environment.TickCount64 - started) >= (lookup.Value.Count == 0 ? _unknownTtl : _catalogTtl);

    IReadOnlyCollection<string> Discover(string backend)
    {
        if (backend is not ("codex" or "pi"))
        {
            return [];
        }

        using var process = new Process();
        var started = false;
        try
        {
            process.StartInfo = new ProcessStartInfo(_binary(backend))
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

            using var document = JsonDocument.Parse(output);
            return [.. document.RootElement.GetProperty("models").EnumerateArray()
                .Where(item => item.TryGetProperty("supported_in_api", out var supported) && supported.ValueKind == JsonValueKind.True
                    && item.TryGetProperty("visibility", out var visibility) && visibility.GetString() == "list"
                    && item.TryGetProperty("slug", out var slug) && slug.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(slug.GetString()))
                .Select(item => item.GetProperty("slug").GetString()!)];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Missing binary, changed output, or deadline: the catalog is unknown.
            if (started && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception killError) when (killError is not OutOfMemoryException) { }
            }
            return [];
        }
    }
}
