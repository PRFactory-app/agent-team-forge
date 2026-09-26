using System.Text.Json;
using AgentTeamForge.DAL.Features.Wake;

namespace AgentTeamForge.Business.Features.Wake;

/// <summary>Append a notice-only doorbell for the Pi extension to inject into its own session.</summary>
public sealed class PiExtensionWake(string stateDirectory) : IWakePoster
{
    public async Task<bool> PostAsync(WakeRegistration target, string notice, CancellationToken cancellationToken)
    {
        if (target.Kind != "pi" || !target.Key.StartsWith("pi:", StringComparison.Ordinal)
            || !int.TryParse(target.Key.AsSpan(3), out var pid) || pid <= 0)
        {
            return false;
        }

        var expected = Path.Combine(stateDirectory, "pi-wake-" + pid + ".jsonl");
        if (target.Address != expected)
        {
            return false;
        }

        try
        {
            var line = JsonSerializer.Serialize(new PiDoorbell(target.Generation, notice), WakeJson.Default.PiDoorbell) + "\n";
            await File.AppendAllTextAsync(expected, line, cancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(expected, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }
}
