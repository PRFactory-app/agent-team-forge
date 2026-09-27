using System.Text.Json;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

internal enum InteractivePlatform { Linux, MacOS, Windows, Other }

/// <summary>Only rejects setup states that can be established before an interactive tab exists.</summary>
internal static class InteractiveAgentPreflight
{
    internal static void CheckCurrent(InteractiveAgentKind kind, string workingDirectory)
    {
        var platform = OperatingSystem.IsLinux() ? InteractivePlatform.Linux
            : OperatingSystem.IsMacOS() ? InteractivePlatform.MacOS
            : OperatingSystem.IsWindows() ? InteractivePlatform.Windows : InteractivePlatform.Other;
        if (Check(kind, Environment.GetEnvironmentVariable, workingDirectory, platform) is { } blocker)
        {
            throw blocker;
        }
    }

    internal static AgentStartupBlockedException? Check(InteractiveAgentKind kind, Func<string, string?> environment,
        string workingDirectory, InteractivePlatform platform)
    {
        if (kind != InteractiveAgentKind.Claude) { return null; }
        try
        {
            var config = ClaudeConfigRoot.Resolve(environment, workingDirectory);
            // With an override Claude keeps global state in the override directory;
            // otherwise it lives beside ~/.claude, at ~/.claude.json.
            var global = environment("CLAUDE_CONFIG_DIR") is { Length: > 0 }
                ? Path.Combine(config, ".claude.json") : Path.Combine(Path.GetDirectoryName(config)!, ".claude.json");
            if (!Missing(global))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(global));
                if (document.RootElement.ValueKind != JsonValueKind.Object) { return null; }
                if (!document.RootElement.TryGetProperty("hasCompletedOnboarding", out var completed)
                    || completed.ValueKind == JsonValueKind.False)
                {
                    return new("agent_first_run_required", "Claude may require first-run setup; run `claude` once in a terminal to finish setup.");
                }
                if (completed.ValueKind != JsonValueKind.True) { return null; }
                // Older configurations can carry account or key state in the
                // global file. Presence is enough to make absence unprovable.
                if (document.RootElement.TryGetProperty("primaryApiKey", out _)
                    || document.RootElement.TryGetProperty("oauthAccount", out _)) { return null; }
            }
            else
            {
                return new("agent_first_run_required", "Claude may require first-run setup; run `claude` once in a terminal to finish setup.");
            }

            // Keychain and Windows credential manager can hold valid OAuth credentials
            // even when .credentials.json does not exist. Never infer login there.
            if (platform != InteractivePlatform.Linux) { return null; }
            if (!Missing(Path.Combine(config, ".credentials.json"))) { return null; }
            if (Has(environment, "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN",
                "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY",
                "CLAUDE_CODE_USE_ANTHROPIC_AWS", "ANTHROPIC_AWS_API_KEY", "ANTHROPIC_FOUNDRY_API_KEY",
                "ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS", "CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR",
                "CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR", "CLAUDE_CODE_GATEWAY_TOKEN_FILE_DESCRIPTOR")) { return null; }

            // A settings file can supply apiKeyHelper or an env block. If it is
            // present, leave any uncertain configuration to the existing bound.
            if (!Missing(Path.Combine(config, "settings.json"))
                || !Missing(Path.Combine(workingDirectory, ".claude", "settings.json"))
                || !Missing(Path.Combine(workingDirectory, ".claude", "settings.local.json"))
                || !Missing("/etc/claude-code/managed-settings.json")) { return null; }
            return new("agent_login_required", "Claude may require login; run `claude` once in a terminal to log in.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // An unreadable or changing config is not proof that setup is missing.
            return null;
        }
    }

    static bool Has(Func<string, string?> environment, params string[] names) =>
        names.Any(name => !string.IsNullOrWhiteSpace(environment(name)));

    // File.Exists maps permission errors to false, which is unsafe for a
    // negative proof. GetAttributes lets the outer catch defer that case.
    static bool Missing(string path)
    {
        try { _ = File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }
}
