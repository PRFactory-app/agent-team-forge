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
    static readonly string[] SecretPaths = ["~/.ssh", "~/.aws", "~/.config/gh", "~/.netrc"];

    /// <summary>
    /// Paths a shell cannot read: the secret locations that exist on this machine (PO decision; a nonexistent path is skipped so Codex does not
    /// fail on it). A maintained list, not a boundary (ADR 0013, guarantee 3).
    /// </summary>
    public static IReadOnlyList<string> DenyRead(string? home = null)
    {
        home ??= Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } configured && Path.IsPathFullyQualified(configured)
            ? configured : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return [.. SecretPaths.Where(p => Path.Exists(Path.Combine(home, p[2..])))];
    }

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
    /// <summary>The secret paths denied to the shell, fixed when the launch is built.</summary>
    public IReadOnlyList<string> DenyRead { get; init; } = SandboxPolicy.DenyRead();

    public const string CodexProfile = "atf";

    static readonly string[] GitWritableNames = ["objects", "refs", "logs"];

    /// <summary>The directories a sandboxed shell can write: the workspace, the worktree's git allowances and the cache directories.</summary>
    public static IReadOnlyList<string> WritableRoots(string workingDirectory, GitLayout? layout)
    {
        List<string> roots = [workingDirectory, .. SandboxPolicy.CacheDirectories];
        if (layout is not null)
        {
            if (layout.Linked) { roots.Add(layout.GitDir); }
            roots.AddRange(GitWritableNames.Select(name => Path.Combine(layout.CommonDir, name)));
        }
        return roots;
    }

    /// <summary>
    /// Whether a writable root contains ATF's state dir (the database, the registry, the settings files and the managed MCP tokens live
    /// there) or reaches into it anywhere but the job worktrees. A job worktree under the state dir is the normal case and is fine.
    /// </summary>
    public static bool OverlapsState(string? stateRoot, string workingDirectory, GitLayout? layout = null)
    {
        if (stateRoot is null) { return false; }
        var state = Canonical(stateRoot);
        var worktrees = Path.Combine(state, "worktrees");
        foreach (var root in WritableRoots(workingDirectory, layout ?? GitLayout.Find(workingDirectory)).Select(Canonical))
        {
            var jobWorktree = root != worktrees && Contains(worktrees, root);
            if (Contains(root, state) || Contains(state, root) && !jobWorktree) { return true; }
        }
        return false;
    }

    /// <summary>Whether <paramref name="child"/> is <paramref name="parent"/> or inside it. A root such as <c>/</c> already ends in a separator.</summary>
    internal static bool Contains(string parent, string child)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return child.Equals(parent, comparison) || child.StartsWith(prefix, comparison);
    }

    static string Canonical(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return AgentTeamForge.Business.Features.Agents.Terminals.PhysicalPath.Resolve(full) is { } physical ? Path.TrimEndingDirectorySeparator(physical) : full;
    }

    /// <summary>Resolves the git layout of the working directory and writes the Claude settings file when the backend needs one.</summary>
    public static SandboxLaunch Create(string workingDirectory, string backend, string settingsDirectory, string jobId, string? home = null, GitLayout? trustedLayout = null)
    {
        var denyRead = SandboxPolicy.DenyRead(home);
        // The caller passes the layout it validated; rediscovering would read metadata the agent can rewrite.
        var layout = trustedLayout ?? GitLayout.Find(workingDirectory);
        string? settings = null;
        if (backend == BackendCatalog.Claude)
        {
            PrivateFiles.CreateDirectory(settingsDirectory);
            settings = Path.Combine(settingsDirectory, jobId + ".claude-settings.json");
            var temporary = settings + ".tmp";
            using (var stream = new FileStream(temporary, PrivateFiles.Options(FileMode.Create, FileAccess.Write)))
            using (var writer = new StreamWriter(stream)) { writer.Write(ClaudeSettings(denyRead).ToJsonString()); }
            File.Move(temporary, settings, overwrite: true);
        }
        return new(layout?.CommonDir ?? "", layout?.GitDir ?? "", settings) { DenyRead = denyRead };
    }

    /// <summary>Claude's sandbox settings (ADR 0013). No unix-socket allowance: `atf mcp` is a stdio server the CLI starts outside the sandbox.</summary>
    internal static JsonObject ClaudeSettings(IReadOnlyList<string> denyRead)
    {
        var sandbox = new JsonObject
        {
            ["enabled"] = true,
            ["autoAllowBashIfSandboxed"] = true,
            ["allowUnsandboxedCommands"] = false,
            ["failIfUnavailable"] = true,
            ["filesystem"] = new JsonObject
            {
                ["denyRead"] = new JsonArray([.. denyRead.Select(p => (JsonNode)p)]),
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
        entries.AddRange(DenyRead.Select(p => (p, "none")));
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
public sealed record GitLayout(string GitDir, string CommonDir, string Root)
{
    public bool Linked => !string.Equals(GitDir, CommonDir, StringComparison.Ordinal);

    public static GitLayout? Find(string directory)
    {
        for (var current = Path.GetFullPath(directory); current is not null; current = Path.GetDirectoryName(current))
        {
            var marker = Path.Combine(current, ".git");
            if (Directory.Exists(marker)) { return new(marker, marker, current); }
            if (!File.Exists(marker)) { continue; }
            var line = File.ReadLines(marker).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
            if (line is null) { return null; }
            var gitDir = Path.GetFullPath(line["gitdir:".Length..].Trim(), current);
            return new(gitDir, ResolveCommon(gitDir), current);
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

/// <summary>
/// The environment a sandboxed launch hands the agent (ADR 0013, "Environment"): an allow-list, not a deny-list, so a credential
/// variable nobody thought of is dropped too. The agent's own sign-in (<c>ANTHROPIC_*</c>, <c>CLAUDE_*</c>, <c>CODEX_*</c>,
/// <c>OPENAI_*</c>) is kept because the CLI starts before any shell the sandbox confines. Cloud credentials (<c>AWS_*</c>,
/// <c>GOOGLE_*</c>), tokens and agent sockets are not on the list.
/// </summary>
public static class SandboxEnvironment
{
    static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "PATH", "HOME", "USER", "LOGNAME", "SHELL", "TERM", "COLORTERM", "LANG", "TZ", "TMPDIR", "TMP", "TEMP", "PWD",
        "DISPLAY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS",
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy",
        "SSL_CERT_FILE", "SSL_CERT_DIR", "NODE_EXTRA_CA_CERTS", "REQUESTS_CA_BUNDLE", "CURL_CA_BUNDLE",
        "CODEX_HOME", "CLAUDE_CONFIG_DIR",
        // Windows: a process without these cannot start.
        "SYSTEMROOT", "WINDIR", "COMSPEC", "PATHEXT", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "PROGRAMDATA", "PROGRAMFILES", "OS",
    };

    internal static readonly string[] Prefixes = ["LC_", "XDG_", "MISE_", "DOTNET_", "CLAUDE_CODE_", "ANTHROPIC_", "OPENAI_", "CODEX_"];

    public static bool IsAllowed(string name) =>
        Names.Contains(name) || Prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))
        || OperatingSystem.IsWindows() && Names.Contains(name.ToUpperInvariant());

    /// <summary>Removes every variable that is not on the allow-list. Run it before ATF adds its own markers.</summary>
    public static void Restrict(IDictionary<string, string?> environment)
    {
        foreach (var name in environment.Keys.Where(n => !IsAllowed(n)).ToList()) { environment.Remove(name); }
    }

    /// <summary>A PowerShell prologue that removes everything off the allow-list from the tab's environment.</summary>
    public static string PowerShellPrologue()
    {
        static string Quote(string value) => "'" + value + "'";
        var exact = string.Join(',', Names.Where(n => n.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')).Select(n => Quote(n.ToUpperInvariant())).Distinct().Order(StringComparer.Ordinal));
        var prefixes = string.Join(" -or ", Prefixes.Select(p => "$n.StartsWith(" + Quote(p) + ", [System.StringComparison]::Ordinal)"));
        return "Get-ChildItem Env: | Where-Object { $n = $_.Name.ToUpperInvariant(); -not (@(" + exact + ") -contains $n -or " + prefixes
            + ") } | ForEach-Object { Remove-Item -LiteralPath ('Env:' + $_.Name) }";
    }

    /// <summary>A POSIX sh prologue that unsets everything off the allow-list in the terminal's own environment.</summary>
    public static string ShellPrologue()
    {
        var exact = string.Join('|', Names.Where(n => n.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')).Order(StringComparer.Ordinal));
        var prefixes = string.Join('|', Prefixes.Select(p => p + "*"));
        return "for name in $(env | cut -d= -f1); do case \"$name\" in " + exact + "|" + prefixes
            + ") ;; *) unset \"$name\" 2>/dev/null || true ;; esac; done\n";
    }
}
