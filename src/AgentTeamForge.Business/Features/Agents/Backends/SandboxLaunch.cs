using System.Text;
using System.Text.Json.Nodes;
using AgentTeamForge.DAL.Files;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>
/// The sandbox policy that A5 (network rule) owns: ADR 0013 fixes the shape, the network and cache values are filled by that item.
/// Until then a sandboxed launch has no network and no package caches.
/// </summary>
public static class SandboxPolicy
{
    /// <summary>Paths a shell cannot read. Maintained list, not a boundary (ADR 0013, guarantee 3).</summary>
    public static readonly IReadOnlyList<string> DenyRead = ["~/.ssh"];

    public static bool NetworkEnabled => false;
    public static IReadOnlyList<string> AllowedDomains => [];
    public static IReadOnlyList<string> CacheDirectories => [];
}

/// <summary>
/// One sandboxed launch: the arguments ADR 0013 gives per backend, built from the job worktree's git layout and a per-job settings file.
/// Absent (null on <see cref="BackendRequest.Sandbox"/>) means today's ADR 0006 bypass flags.
/// </summary>
public sealed record SandboxLaunch(string GitCommonDir, string GitDir, string? ClaudeSettingsPath)
{
    public const string CodexProfile = "atf";

    static readonly string[] GitWritableNames = ["objects", "refs", "logs"];

    /// <summary>Resolves the git layout of the working directory and writes the Claude settings file when the backend needs one.</summary>
    public static SandboxLaunch Create(string workingDirectory, string backend, string settingsDirectory, string jobId)
    {
        var layout = GitLayout.Find(workingDirectory);
        string? settings = null;
        if (backend == BackendCatalog.Claude)
        {
            PrivateFiles.CreateDirectory(settingsDirectory);
            settings = Path.Combine(settingsDirectory, jobId + ".claude-settings.json");
            var temporary = settings + ".tmp";
            using (var stream = new FileStream(temporary, PrivateFiles.Options(FileMode.Create, FileAccess.Write)))
            using (var writer = new StreamWriter(stream)) { writer.Write(ClaudeSettings().ToJsonString()); }
            File.Move(temporary, settings, overwrite: true);
        }
        return new(layout?.CommonDir ?? "", layout?.GitDir ?? "", settings);
    }

    /// <summary>Claude's sandbox settings (ADR 0013). No unix-socket allowance: `atf mcp` is a stdio server the CLI starts outside the sandbox.</summary>
    internal static JsonObject ClaudeSettings()
    {
        var sandbox = new JsonObject
        {
            ["enabled"] = true,
            ["autoAllowBashIfSandboxed"] = true,
            ["allowUnsandboxedCommands"] = false,
            ["failIfUnavailable"] = true,
            ["filesystem"] = new JsonObject
            {
                ["denyRead"] = new JsonArray([.. SandboxPolicy.DenyRead.Select(p => (JsonNode)p)]),
                ["allowWrite"] = new JsonArray([.. SandboxPolicy.CacheDirectories.Select(p => (JsonNode)p)]),
            },
        };
        if (SandboxPolicy.NetworkEnabled) { sandbox["network"] = new JsonObject { ["allowedDomains"] = new JsonArray([.. SandboxPolicy.AllowedDomains.Select(p => (JsonNode)p)]) }; }
        return new JsonObject
        {
            ["permissions"] = new JsonObject { ["allow"] = new JsonArray("Edit(./**)", "Read(./**)", "mcp__agentteamforge") },
            ["sandbox"] = sandbox,
            ["disableAllHooks"] = true,
        };
    }

    /// <summary>The Codex permission profile for `exec` and the TUI. `managedMcp` adds the approval mode the launch's MCP server needs under approval never.</summary>
    public IReadOnlyList<string> CodexArguments(bool managedMcp)
    {
        List<(string Path, string Access)> entries = [("/", "read"), (":workspace_roots", "write")];
        entries.AddRange(SandboxPolicy.DenyRead.Select(p => (p, "none")));
        // Hooks and config stay read-only; only the worktree admin dir, objects, refs and logs can be written (ADR 0013, guarantee 2).
        if (GitDir.Length > 0 && GitCommonDir.Length > 0)
        {
            if (!string.Equals(GitDir, GitCommonDir, StringComparison.Ordinal)) { entries.Add((GitDir, "write")); }
            entries.AddRange(GitWritableNames.Select(name => (Path.Combine(GitCommonDir, name), "write")));
        }
        entries.AddRange(SandboxPolicy.CacheDirectories.Select(p => (p, "write")));
        var args = new List<string>
        {
            "-c", "permissions." + CodexProfile + ".filesystem={" + string.Join(",", entries.Select(e => TomlString(e.Path) + "=" + TomlString(e.Access))) + "}",
            "-c", "permissions." + CodexProfile + ".network.enabled=" + (SandboxPolicy.NetworkEnabled ? "true" : "false"),
            "-c", "default_permissions=" + TomlString(CodexProfile),
        };
        if (managedMcp) { args.AddRange(["-c", "mcp_servers.agentteamforge.default_tools_approval_mode=" + TomlString("approve")]); }
        return args;
    }

    /// <summary>Claude's sandbox flags, replacing the bypass flags: dontAsk, no user/project/local settings, only ATF's settings file and MCP config.</summary>
    public IReadOnlyList<string> ClaudeArguments() =>
        ["--permission-mode", "dontAsk", "--setting-sources", "", "--settings", ClaudeSettingsPath ?? throw new InvalidOperationException("sandboxed Claude has no settings file"), "--strict-mcp-config"];

    /// <summary>TOML literal string where possible (no double quotes for Windows PowerShell 5.1), a basic string otherwise.</summary>
    internal static string TomlString(string value)
    {
        if (!value.Any(c => c is '\'' or < ' ' or '\u007f')) { return "'" + value + "'"; }
        var text = new StringBuilder("\"");
        foreach (var c in value)
        {
            if (c is '\\' or '"') { text.Append('\\').Append(c); }
            else if (c is < ' ' or '\u007f') { text.Append("\\u").Append(((int)c).ToString("X4")); }
            else { text.Append(c); }
        }
        return text.Append('"').ToString();
    }
}

/// <summary>Where a working directory's git data lives: <c>.git</c> is a directory, or a file naming a linked worktree's admin dir.</summary>
public sealed record GitLayout(string GitDir, string CommonDir)
{
    public static GitLayout? Find(string directory)
    {
        for (var current = Path.GetFullPath(directory); current is not null; current = Path.GetDirectoryName(current))
        {
            var marker = Path.Combine(current, ".git");
            if (Directory.Exists(marker)) { return new(marker, marker); }
            if (!File.Exists(marker)) { continue; }
            var line = File.ReadLines(marker).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
            if (line is null) { return null; }
            var gitDir = Path.GetFullPath(line["gitdir:".Length..].Trim(), current);
            return new(gitDir, ResolveCommon(gitDir));
        }
        return null;
    }

    /// <summary>The admin dir's <c>commondir</c> pointer, relative to it; a dir without one is its own common dir.</summary>
    public static string ResolveCommon(string gitDir)
    {
        var pointer = Path.Combine(gitDir, "commondir");
        return File.Exists(pointer) ? Path.GetFullPath(File.ReadAllText(pointer).Trim(), gitDir) : gitDir;
    }
}

/// <summary>Credential environment variables a sandboxed launch does not hand to the agent (ADR 0013, "Environment").</summary>
public static class SandboxEnvironment
{
    static readonly string[] ExactNames =
    [
        "GITHUB_TOKEN", "GH_TOKEN", "GH_ENTERPRISE_TOKEN", "GITLAB_TOKEN", "BITBUCKET_TOKEN", "NPM_TOKEN", "NODE_AUTH_TOKEN",
        "NUGET_API_KEY", "HF_TOKEN", "SSH_AUTH_SOCK", "GPG_AGENT_INFO", "DOCKER_CONFIG", "KUBECONFIG", "VAULT_TOKEN", "OP_SERVICE_ACCOUNT_TOKEN",
    ];

    static readonly string[] Prefixes = ["AWS_", "AZURE_", "ARM_", "GOOGLE_", "GCP_", "GCLOUD_", "CLOUDSDK_", "DIGITALOCEAN_", "TF_VAR_"];

    static readonly string[] Suffixes = ["_TOKEN", "_SECRET", "_PASSWORD", "_API_KEY", "_ACCESS_KEY", "_SECRET_KEY", "_CREDENTIALS", "_PAT"];

    // The agent CLI's own sign-in must survive; the CLI starts before any shell the sandbox confines.
    static readonly string[] KeptPrefixes = ["ANTHROPIC_", "CLAUDE_", "CODEX_", "OPENAI_"];

    public static bool IsCredential(string name)
    {
        if (KeptPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) { return false; }
        return ExactNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || Prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || Suffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    public static void Scrub(IDictionary<string, string?> environment)
    {
        foreach (var name in environment.Keys.Where(IsCredential).ToList()) { environment.Remove(name); }
    }

    /// <summary>The variables of this process that a sandboxed terminal launch must not inherit.</summary>
    public static IReadOnlyList<string> PresentCredentials() =>
        [.. Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(IsCredential).Order(StringComparer.Ordinal)];
}
