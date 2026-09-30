using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Setup;

/// <summary>Shared live retention limits, persisted with the other launch settings.</summary>
public sealed class InteractiveRetentionConfiguration
{
    readonly Lock _gate = new();
    InteractiveRetentionSettings _current = new();
    readonly Func<LaunchModeSettings?> _read;
    readonly Action<LaunchModeSettings> _write;

    public InteractiveRetentionConfiguration(StateDirectory state)
        : this(() => SetupCommand.ConfiguredSettings(state), settings => SetupCommand.WriteMode(state, settings)) { }

    internal InteractiveRetentionConfiguration(Func<LaunchModeSettings?> read, Action<LaunchModeSettings> write)
    {
        _read = read;
        _write = write;
    }

    public InteractiveRetentionSettings Current
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    if (_read() is { } settings)
                    {
                        _current = new(settings.MaxRetainedSessions, settings.IdleCloseMinutes);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StateDirectoryException) { }
                return _current;
            }
        }
    }

    public IpcResponse Handle(IpcRequest request)
    {
        if (request.Op == IpcProtocol.RetentionSettingsGet) { return new(true, RetentionSettings: Current); }
        if (request.Op != IpcProtocol.RetentionSettingsPut || request.MaxRetainedSessions is not { } cap
            || request.IdleCloseMinutes is not { } minutes || !new InteractiveRetentionSettings(cap, minutes).IsValid())
        {
            return new(false, JobErrors.InvalidRequest);
        }
        lock (_gate)
        {
            try
            {
                if (_read() is not { } settings) { return new(false, JobErrors.InvalidRequest); }
                _write(settings with { MaxRetainedSessions = cap, IdleCloseMinutes = minutes });
                _current = new(cap, minutes);
                return new(true, RetentionSettings: _current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StateDirectoryException)
            {
                return new(false, JobErrors.StorageUnavailable);
            }
        }
    }
}
