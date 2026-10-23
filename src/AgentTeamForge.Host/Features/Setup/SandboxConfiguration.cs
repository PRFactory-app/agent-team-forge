using System.Text.Json.Nodes;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Files;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Setup;

/// <summary>
/// What the Settings page shows: the values in force, the defaults to restore, configured cache paths this machine lacks, and the read
/// error when profile.json cannot be read and the last good settings stay in force.
/// </summary>
public sealed record SandboxSettingsView(bool Network, IReadOnlyList<string> AllowedDomains, IReadOnlyList<string> CachePaths,
    IReadOnlyList<string> DefaultAllowedDomains, IReadOnlyList<string> DefaultCachePaths, IReadOnlyList<string> SkippedCachePaths,
    string? ProfileError = null);

/// <summary>
/// This machine's sandbox settings, kept in profile.json and read again at each sandboxed launch, so a saved change applies to the next
/// launch without a daemon restart. A profile that cannot be read keeps the last good settings, which start as the settings the daemon
/// validated at start (<paramref name="startup"/>), never the defaults.
/// </summary>
public sealed class SandboxConfiguration(StateDirectory state, SandboxSettings startup, Action<string>? log = null)
{
    public const int MaxDomains = 200;
    public const int MaxCachePaths = 100;
    readonly Lock _gate = new();
    SandboxSettings _current = startup;
    string? _lastError;

    public SandboxSettings Current
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    _current = ProfileFile.Load(state).Sandbox;
                    _lastError = null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StateDirectoryException or ArgumentException)
                {
                    if (_lastError != ex.Message) { log?.Invoke($"sandbox settings: keeping the last good settings: {ex.Message}"); }
                    _lastError = ex.Message;
                }
                return _current;
            }
        }
    }

    public IpcResponse Handle(IpcRequest request)
    {
        if (request.Op == IpcProtocol.SandboxSettingsGet)
        {
            var current = Current;
            return new(true, Outcome: "sandbox_settings", Sandbox: View(current) with { ProfileError = _lastError });
        }
        if (request.Op != IpcProtocol.SandboxSettingsPut || request.SandboxNetwork is not { } network
            || request.SandboxAllowedDomains is not { } domains || request.SandboxCachePaths is not { } caches)
        {
            return new(false, JobErrors.InvalidRequest);
        }
        if (domains.Count > MaxDomains || caches.Count > MaxCachePaths)
        {
            return new(false, JobErrors.InvalidRequest, ErrorDetail: $"List at most {MaxDomains} domains and {MaxCachePaths} cache paths.");
        }
        // A JSON null element stays null here, so the shared validator refuses it instead of a trim throwing.
        var settings = new SandboxSettings(network, [.. domains.Select(d => d?.Trim()!)], [.. caches.Select(p => p?.Trim()!)]);
        if (settings.Errors(stateRoot: state.Path) is [_, ..] errors)
        {
            return new(false, JobErrors.InvalidRequest, ErrorDetail: string.Join("; ", errors));
        }
        lock (_gate)
        {
            try
            {
                Write(settings);
                _current = ProfileFile.Load(state).Sandbox;
                return new(true, Outcome: "sandbox_settings", Sandbox: View(_current));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StateDirectoryException or System.Text.Json.JsonException)
            {
                return new(false, JobErrors.StorageUnavailable, ErrorDetail: ex.Message);
            }
        }
    }

    static SandboxSettingsView View(SandboxSettings settings)
    {
        var home = SandboxPolicy.Home();
        return new(settings.Network, settings.AllowedDomains, settings.CachePaths, SandboxSettings.Default.AllowedDomains,
            SandboxSettings.Default.CachePaths,
            [.. settings.CachePaths.Where(p => SandboxPolicy.ExpandPath(p, home) is not { } path || !Directory.Exists(path))]);
    }

    /// <summary>Rewrites only the three sandbox keys of profile.json, keeping every other key as it is, and replaces the file atomically.</summary>
    void Write(SandboxSettings settings)
    {
        var profile = JsonNode.Parse(File.ReadAllText(state.ProfileFile))?.AsObject()
            ?? throw new StateDirectoryException("profile_invalid", state.ProfileFile);
        profile["sandbox_network"] = settings.Network;
        profile["sandbox_allowed_domains"] = new JsonArray([.. settings.AllowedDomains.Select(d => (JsonNode)d)]);
        profile["sandbox_cache_paths"] = new JsonArray([.. settings.CachePaths.Select(p => (JsonNode)p)]);
        var temporary = state.ProfileFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, PrivateFiles.Options(FileMode.CreateNew, FileAccess.Write)))
            using (var writer = new System.Text.Json.Utf8JsonWriter(file, new() { Indented = true }))
            {
                profile.WriteTo(writer);
                writer.Flush();
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, state.ProfileFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) { File.Delete(temporary); }
        }
    }
}
