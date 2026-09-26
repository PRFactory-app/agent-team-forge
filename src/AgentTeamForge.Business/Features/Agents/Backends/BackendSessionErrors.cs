using System.Text;

namespace AgentTeamForge.Business.Features.Agents.Backends;

internal static class BackendSessionErrors
{
    // CLI diagnostics vary by backend/version. Keep the stderr pipe drained and
    // retain only enough text to identify a missing native session.
    public static async Task<string> ReadStderrAsync(Stream stream)
    {
        var buffer = new byte[4096];
        var text = new StringBuilder();
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                if (text.Length < 4096)
                {
                    text.Append(Encoding.UTF8.GetString(buffer, 0, Math.Min(read, 4096 - text.Length)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }

        return text.ToString();
    }

    public static bool IsExpired(string diagnostic) =>
        diagnostic.Contains("no rollout found", StringComparison.OrdinalIgnoreCase)
        || diagnostic.Contains("session not found", StringComparison.OrdinalIgnoreCase)
        || diagnostic.Contains("no session found", StringComparison.OrdinalIgnoreCase)
        || diagnostic.Contains("conversation not found", StringComparison.OrdinalIgnoreCase)
        || diagnostic.Contains("no conversation found", StringComparison.OrdinalIgnoreCase)
        || diagnostic.Contains("could not find session", StringComparison.OrdinalIgnoreCase);

    public static async Task<bool> HasExpiredDiagnosticAsync(Task<string> stderr, CancellationToken cancellationToken)
    {
        try
        {
            return IsExpired(await stderr.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken));
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
