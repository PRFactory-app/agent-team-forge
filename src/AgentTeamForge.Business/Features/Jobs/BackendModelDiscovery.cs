using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>One bounded CLI catalog lookup per backend and daemon lifetime.</summary>
public sealed class BackendModelDiscovery
{
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    readonly ConcurrentDictionary<string, Lazy<IReadOnlyCollection<string>>> _cache = new();
    readonly TimeSpan _timeout;
    readonly Func<string, string> _binary;

    public BackendModelDiscovery() : this(DefaultTimeout, backend => backend) { }

    internal BackendModelDiscovery(TimeSpan timeout, Func<string, string> binary)
    {
        _timeout = timeout;
        _binary = binary;
    }

    public IReadOnlyCollection<string> GetModels(string backend) =>
        _cache.GetOrAdd(backend, name => new Lazy<IReadOnlyCollection<string>>(() => Discover(name))).Value;

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
