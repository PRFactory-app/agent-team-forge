using System.Text;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>
/// Recognises a CLI's own signed-out diagnostic and maps it to the terminal
/// <c>agent_login_required</c> error Claude already reports. Callers pass only a
/// CLI error channel (stderr, a failed-turn or <c>is_error</c> result record),
/// never assistant prose, so a reply that merely mentions logging in is not a failure.
/// </summary>
internal static class BackendLoginErrors
{
    public const string Code = "agent_login_required";

    const string PiHint = "Pi has no login or API key for the selected model; run `pi` and /login.";

    const int MaxDiagnosticChars = 500;

    public static BackendEvidence.AgentError? Inspect(string backend, string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return null;
        }

        bool Has(string text) => diagnostic.Contains(text, StringComparison.OrdinalIgnoreCase);
        var (hint, key) = backend switch
        {
            "codex" when Has("401 Unauthorized") => ("Codex is not logged in; run `codex login`.", "401 Unauthorized"),
            "pi" when Has("No API key found") => (PiHint, "No API key found"),
            // Pi's own verdict when no provider has any credential (`pi --list-models`, interactive startup).
            "pi" when Has("No models available") && Has("/login") => (PiHint, "No models available"),
            "droid" when Has("Authentication failed") && (Has("/login") || Has("FACTORY_API_KEY")) =>
                ("Droid is not logged in; run `droid` and /login, or set FACTORY_API_KEY.", "Authentication failed"),
            "cursor-agent" when Has("Authentication required") && (Has("agent login") || Has("CURSOR_API_KEY")) =>
                ("Cursor Agent is not logged in; run `cursor-agent login`, or set CURSOR_API_KEY.", "Authentication required"),
            _ => ((string?)null, ""),
        };
        if (hint is null)
        {
            return null;
        }

        // Keep the CLI's own line (not a trailing docs path) so the reason stays readable.
        var line = diagnostic.Split('\n').First(l => l.Contains(key, StringComparison.OrdinalIgnoreCase)).Trim();
        return new(Code, hint + " " + (line.Length > MaxDiagnosticChars ? line[..MaxDiagnosticChars] + "…" : line));
    }

    /// <summary>The precise sign-in step for a backend's CLI; null for a backend without one.</summary>
    public static string? LoginStep(string? backend) => backend switch
    {
        "claude" => "run `claude` and /login",
        "codex" => "run `codex login`",
        "pi" => "run `pi` and /login, or set the API key of the selected model's provider",
        "droid" => "run `droid` and /login, or set FACTORY_API_KEY",
        "cursor-agent" => "run `cursor-agent login`, or set CURSOR_API_KEY",
        _ => null,
    };

    /// <summary>Codex retries a 401 for minutes; one without any credential cannot recover, so it ends the turn at once.</summary>
    public static bool IsCodexMissingCredential(string? message) =>
        message is not null
        && message.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase)
        && message.Contains("Missing bearer or basic authentication", StringComparison.OrdinalIgnoreCase);

    /// <summary>Inspects stderr once the CLI has closed it, waiting at most a second.</summary>
    public static async Task<BackendEvidence.AgentError?> InspectStderrAsync(string backend, Task<string> stderr, CancellationToken cancellationToken)
    {
        try
        {
            return Inspect(backend, await stderr.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken));
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Writes the reason to the job log, so the latest activity names it instead of the CLI's last stderr line.</summary>
    public static void Report(Action<string, ReadOnlyMemory<byte>>? output, BackendEvidence.AgentError error) =>
        output?.Invoke("status", Encoding.UTF8.GetBytes(error.Details.Replace('\n', ' ') + "\n"));
}
