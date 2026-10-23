using System.Text;
using System.Text.Json.Nodes;
using AgentTeamForge.DAL.Files;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>
/// The sandbox policy that A5 (network rule) owns: ADR 0013 fixes the shape. Network stays on by default (PRFactory contract). Each machine
/// can turn it off and edit the Claude domain list and the writable cache paths (<see cref="SandboxSettings"/>, kept in profile.json).
/// </summary>
public static partial class SandboxPolicy
{
    static readonly string[] SecretPaths = ["~/.ssh", "~/.aws", "~/.config/gh", "~/.netrc"];

    /// <summary>The home directory the sandbox paths are relative to: an absolute <c>HOME</c>, else the user profile.</summary>
    public static string Home() =>
        Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } configured && Path.IsPathFullyQualified(configured)
            ? configured : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Paths a shell cannot read: the secret locations that exist on this machine (PO decision; a nonexistent path is skipped so Codex does not
    /// fail on it). A maintained list, not a boundary (ADR 0013, guarantee 3).
    /// </summary>
    public static IReadOnlyList<string> DenyRead(string? home = null)
    {
        home ??= Home();
        return [.. SecretPaths.Where(p => Path.Exists(Path.Combine(home, p[2..])))];
    }

    /// <summary>
    /// The default Claude domain list. Claude's sandbox proxy reaches only listed domains and refuses a bare <c>*</c>, so "network on" for
    /// Claude is this list of git hosts and package registries. Codex network on is unrestricted (ADR 0013, v1).
    /// </summary>
    public static IReadOnlyList<string> DefaultAllowedDomains { get; } =
    [
        "github.com", "*.github.com", "*.githubusercontent.com", "gitlab.com", "*.gitlab.com", "bitbucket.org", "*.bitbucket.org",
        "dev.azure.com", "*.visualstudio.com", "api.nuget.org", "*.nuget.org", "dot.net", "*.dot.net", "registry.npmjs.org",
        "*.npmjs.org", "registry.yarnpkg.com", "pypi.org", "files.pythonhosted.org", "proxy.golang.org", "sum.golang.org",
        "crates.io", "*.crates.io", "repo.maven.apache.org", "repo1.maven.org",
    ];

    /// <summary>
    /// The default writable package caches, as the tools place them on this OS: NuGet (packages, its HTTP and plugin cache and, on Linux, the
    /// <c>/tmp/.dotnet</c> named-mutex directory restores need), npm, yarn, pip, Cargo and the Go module cache.
    /// </summary>
    public static IReadOnlyList<string> DefaultCachePaths() =>
        OperatingSystem.IsWindows()
            ? ["~/.nuget/packages", "~/AppData/Local/NuGet", "~/AppData/Local/npm-cache", "~/AppData/Local/Yarn/Cache", "~/AppData/Local/pip/Cache",
                "~/.cargo/registry", "~/.cargo/git", "~/go/pkg/mod"]
            : OperatingSystem.IsMacOS()
                ? ["~/.nuget/packages", "~/.local/share/NuGet", "~/.npm", "~/Library/Caches/Yarn", "~/Library/Caches/pip",
                    "~/.cargo/registry", "~/.cargo/git", "~/go/pkg/mod"]
                : ["~/.nuget/packages", "~/.local/share/NuGet", "/tmp/.dotnet", "~/.npm", "~/.cache/yarn", "~/.cache/pip",
                    "~/.cargo/registry", "~/.cargo/git", "~/go/pkg/mod"];

    [System.Text.RegularExpressions.GeneratedRegex(@"^(\*\.)?([a-z0-9]([a-z0-9-]*[a-z0-9])?\.)+[a-z0-9]([a-z0-9-]*[a-z0-9])?\z")]
    private static partial System.Text.RegularExpressions.Regex DomainPattern();

    /// <summary>
    /// Why Claude would reject this domain entry, or null when it is valid: a host (<c>github.com</c>) or a subdomain pattern
    /// (<c>*.github.com</c>) in lower case. A bare <c>*</c>, a scheme, a path, a port and a wildcard over a top-level domain (<c>*.com</c>)
    /// are refused.
    /// </summary>
    public static string? DomainError(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) { return "a domain cannot be empty"; }
        if (domain.Trim('*', '.').Length == 0) { return $"\"{Printable(domain)}\" allows every domain; Claude refuses an allow-all entry"; }
        if (domain.Length > 253 || !DomainPattern().IsMatch(domain))
        {
            return $"\"{Printable(domain)}\" is not a host name or a \"*.\" subdomain pattern (lower case, no scheme, path or port)";
        }
        if (domain.StartsWith("*.", StringComparison.Ordinal) && !domain[2..].Contains('.'))
        {
            return $"\"{domain}\" is too broad: a wildcard needs a registered domain, for example \"*.example.com\"";
        }
        return null;
    }

    /// <summary>
    /// The absolute path of a cache entry: <c>~/</c> is the home directory. Null when the entry is neither absolute nor home-relative, or is
    /// not a valid path (a NUL or other invalid character).
    /// </summary>
    public static string? ExpandPath(string? entry, string home)
    {
        if (string.IsNullOrWhiteSpace(entry) || entry.Contains('\0')) { return null; }
        var path = entry == "~" ? home
            : entry.StartsWith("~/", StringComparison.Ordinal) || entry.StartsWith("~\\", StringComparison.Ordinal) ? Path.Combine(home, entry[2..])
            : entry;
        try { return Path.IsPathFullyQualified(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>
    /// Why this cache entry cannot be writable, or null when it can. Refused: an empty, relative or invalid path, the file-system root, the
    /// home directory or one of its parents, and any path that is, contains or lies inside a secret path (<c>~/.ssh</c>, <c>~/.aws</c>,
    /// <c>~/.config/gh</c>, <c>~/.netrc</c>; hard rule) or ATF's state directory. Both sides are compared as written and as resolved
    /// through symlinks, so neither a cache link into <c>~/.ssh</c> nor a cache under the target of a symlinked <c>~/.ssh</c> passes. A path
    /// that cannot be resolved is refused.
    /// </summary>
    public static string? CachePathError(string? entry, string? home = null, string? stateRoot = null) => CheckCachePath(entry, home ?? Home(), stateRoot).Error;

    /// <summary>Test seam: resolves the cache side, as a link that another process retargets between two resolutions can.</summary>
    internal static readonly AsyncLocal<Func<string, string?>?> CacheResolver = new();

    /// <summary>
    /// Resolves the cache entry once and checks that snapshot (the written path and its physical path). The physical path returned is the one
    /// that was checked, so a caller that grants it cannot grant a target that a later resolution would find.
    /// </summary>
    static (string? Error, string? Physical) CheckCachePath(string? entry, string home, string? stateRoot)
    {
        if (string.IsNullOrWhiteSpace(entry)) { return ("a cache path cannot be empty", null); }
        if (ExpandPath(entry, home) is not { } path) { return ($"\"{Printable(entry)}\" must be a valid absolute path or start with ~/", null); }
        if ((CacheResolver.Value ?? Terminals.PhysicalPath.Resolve)(path) is not { } resolved)
        {
            return ($"\"{entry}\" cannot be resolved through its symlinks", null);
        }
        var physical = Path.TrimEndingDirectorySeparator(resolved);
        IReadOnlyList<string> caches = physical == path ? [path] : [path, physical];
        return (CacheOverlapError(entry, path, caches, home, stateRoot), physical);
    }

    static string? CacheOverlapError(string entry, string path, IReadOnlyList<string> caches, string home, string? stateRoot)
    {
        var fullHome = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        if (Aliases(fullHome) is not { } homes) { return "the home directory cannot be resolved through its symlinks"; }
        if (Path.GetPathRoot(path) == path || caches.Any(c => homes.Any(h => SandboxLaunch.Contains(c, h))))
        {
            return $"\"{entry}\" is too broad: it contains the home directory";
        }
        foreach (var secret in SecretPaths)
        {
            if (Aliases(Path.Combine(fullHome, secret[2..])) is not { } secrets) { return $"{secret} cannot be resolved, so no cache path is allowed"; }
            if (Overlap(caches, secrets)) { return $"\"{entry}\" overlaps {secret}, which always stays denied"; }
        }
        if (stateRoot is not null)
        {
            if (Aliases(Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateRoot))) is not { } states) { return "ATF's state directory cannot be resolved"; }
            if (Overlap(caches, states)) { return $"\"{entry}\" overlaps ATF's state directory"; }
        }
        return null;
    }

    static string Printable(string entry) => entry.Replace("\0", "\\0", StringComparison.Ordinal);

    static bool Overlap(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Any(l => right.Any(r => SandboxLaunch.Contains(l, r) || SandboxLaunch.Contains(r, l)));

    /// <summary>
    /// The path as written and as resolved through every symlink in it (also a missing tail under a linked parent). Null when resolution
    /// fails (a link loop or an unreadable link): the caller refuses what it cannot compare.
    /// </summary>
    static IReadOnlyList<string>? Aliases(string path)
    {
        if (Terminals.PhysicalPath.Resolve(path) is not { } physical) { return null; }
        var resolved = Path.TrimEndingDirectorySeparator(physical);
        return resolved == path ? [path] : [path, resolved];
    }

    /// <summary>
    /// The cache paths a launch makes writable: each configured entry that passes <see cref="CachePathError"/> and exists, as the physical
    /// path the check validated (every symlink resolved), never the configured spelling: a link retargeted after the policy is written
    /// cannot move the grant. An entry that fails the check is dropped here too (a hand-edited profile cannot bypass it), and a missing
    /// directory is skipped rather than created: Codex refuses a nonexistent path, and ATF does not create tool directories the machine does
    /// not use. A path inside <paramref name="writableRoots"/> or inside another cache is dropped as well: the agent can write its parent and
    /// could swap a component for a link, and the outer grant already covers it.
    /// </summary>
    public static IReadOnlyList<string> ResolveCachePaths(IEnumerable<string?> configured, string? home = null, string? stateRoot = null,
        IEnumerable<string>? writableRoots = null)
    {
        home ??= Home();
        // One resolution per entry: the snapshot that passed the check is the path granted.
        List<string> physical = [.. configured.Select(entry => CheckCachePath(entry, home, stateRoot))
            .Where(c => c.Error is null).Select(c => c.Physical!).Where(Directory.Exists).Distinct(StringComparer.Ordinal)];
        List<string> roots = [.. (writableRoots ?? []).Select(r => Terminals.PhysicalPath.Resolve(Path.GetFullPath(r)) ?? Path.GetFullPath(r))
            .Select(Path.TrimEndingDirectorySeparator)];
        return [.. physical.Where(path => !roots.Any(root => SandboxLaunch.Contains(root, path))
            && !physical.Any(other => other != path && SandboxLaunch.Contains(other, path)))];
    }
}

/// <summary>
/// This machine's sandbox settings (profile.json): shell network on or off, Claude's domain list, and the cache paths both CLIs may write.
/// Null lists in the profile mean the defaults.
/// </summary>
public sealed record SandboxSettings(bool Network, IReadOnlyList<string> AllowedDomains, IReadOnlyList<string> CachePaths)
{
    public static SandboxSettings Default { get; } = new(true, SandboxPolicy.DefaultAllowedDomains, SandboxPolicy.DefaultCachePaths());

    public static SandboxSettings From(bool? network, IReadOnlyList<string>? allowedDomains, IReadOnlyList<string>? cachePaths) =>
        new(network != false, allowedDomains ?? Default.AllowedDomains, cachePaths ?? Default.CachePaths);

    /// <summary>Every validation error of the lists, empty when they are valid.</summary>
    public IReadOnlyList<string> Errors(string? home = null, string? stateRoot = null) =>
    [
        .. AllowedDomains.Select(SandboxPolicy.DomainError).OfType<string>(),
        .. CachePaths.Select(p => SandboxPolicy.CachePathError(p, home, stateRoot)).OfType<string>(),
    ];
}

/// <summary>
/// One sandboxed launch: the arguments ADR 0013 gives per backend, built from the job worktree's git layout and a per-job settings file.
/// Absent (null on <see cref="BackendRequest.Sandbox"/>) means today's ADR 0006 bypass flags.
/// </summary>
public sealed record SandboxLaunch(string GitCommonDir, string GitDir, string? ClaudeSettingsPath)
{
    /// <summary>The secret paths denied to the shell, fixed when the launch is built.</summary>
    public IReadOnlyList<string> DenyRead { get; init; } = SandboxPolicy.DenyRead();

    /// <summary>Shell network access: on unless this machine's profile opted out (<c>sandbox_network: false</c>).</summary>
    public bool Network { get; init; } = true;

    /// <summary>The domains Claude's proxy reaches when the network is on.</summary>
    public IReadOnlyList<string> AllowedDomains { get; init; } = SandboxPolicy.DefaultAllowedDomains;

    /// <summary>The resolved, existing cache directories both CLIs may write.</summary>
    public IReadOnlyList<string> CachePaths { get; init; } = [];

    public const string CodexProfile = "atf";

    /// <summary>The state subdirectory that holds the PRFactory connector's team workspaces.</summary>
    public const string PRFactoryWorkspaces = "prfactory-workspaces";

    static readonly string[] GitWritableNames = ["objects", "refs", "logs"];

    /// <summary>
    /// The directories a sandboxed shell can write besides the cache paths: the workspace and the worktree's git allowances. Cache paths
    /// that overlap the state dir are dropped when they are resolved (<see cref="SandboxPolicy.ResolveCachePaths"/>).
    /// </summary>
    public static IReadOnlyList<string> WritableRoots(string workingDirectory, GitLayout? layout)
    {
        List<string> roots = [workingDirectory];
        if (layout is not null)
        {
            if (layout.Linked) { roots.Add(layout.GitDir); }
            roots.AddRange(GitWritableNames.Select(name => Path.Combine(layout.CommonDir, name)));
        }
        return roots;
    }

    /// <summary>
    /// Whether a writable root contains ATF's state dir (the database, the registry, the settings files and the managed MCP tokens live
    /// there) or reaches into it anywhere but the job worktrees and the PRFactory team workspaces. A workspace under the state dir is the
    /// normal case and is fine.
    /// </summary>
    public static bool OverlapsState(string? stateRoot, string workingDirectory, GitLayout? layout = null)
    {
        if (stateRoot is null) { return false; }
        var state = Canonical(stateRoot);
        string[] workspaces = [Path.Combine(state, "worktrees"), Path.Combine(state, PRFactoryWorkspaces)];
        foreach (var root in WritableRoots(workingDirectory, layout ?? GitLayout.Find(workingDirectory)).Select(Canonical))
        {
            var jobWorktree = workspaces.Any(parent => root != parent && Contains(parent, root));
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
    public static SandboxLaunch Create(string workingDirectory, string backend, string settingsDirectory, string jobId, string? home = null, GitLayout? trustedLayout = null,
        SandboxSettings? settings = null, string? stateRoot = null)
    {
        home ??= SandboxPolicy.Home();
        settings ??= SandboxSettings.Default;
        var denyRead = SandboxPolicy.DenyRead(home);

        // An invalid domain makes Claude refuse the settings file, so a hand-edited entry is dropped instead.
        IReadOnlyList<string> domains = [.. settings.AllowedDomains.Where(d => SandboxPolicy.DomainError(d) is null)];
        // The caller passes the layout it validated; rediscovering would read metadata the agent can rewrite.
        var layout = trustedLayout ?? GitLayout.Find(workingDirectory);
        var caches = SandboxPolicy.ResolveCachePaths(settings.CachePaths, home, stateRoot, WritableRoots(workingDirectory, layout));
        string? settingsFile = null;
        if (backend == BackendCatalog.Claude)
        {
            PrivateFiles.CreateDirectory(settingsDirectory);
            settingsFile = Path.Combine(settingsDirectory, jobId + ".claude-settings.json");
            var temporary = settingsFile + ".tmp";
            using (var stream = new FileStream(temporary, PrivateFiles.Options(FileMode.Create, FileAccess.Write)))
            using (var writer = new StreamWriter(stream)) { writer.Write(ClaudeSettings(denyRead, settings.Network ? domains : null, caches).ToJsonString()); }
            File.Move(temporary, settingsFile, overwrite: true);
        }
        return new(layout?.CommonDir ?? "", layout?.GitDir ?? "", settingsFile)
        {
            DenyRead = denyRead,
            Network = settings.Network,
            AllowedDomains = domains,
            CachePaths = caches,
        };
    }

    /// <summary>
    /// Claude's sandbox settings (ADR 0013). No unix-socket allowance: `atf mcp` is a stdio server the CLI starts outside the sandbox.
    /// Null <paramref name="allowedDomains"/> is network off: without a network entry the proxy denies every domain under dontAsk.
    /// </summary>
    internal static JsonObject ClaudeSettings(IReadOnlyList<string> denyRead, IReadOnlyList<string>? allowedDomains, IReadOnlyList<string> cachePaths)
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
                ["allowWrite"] = new JsonArray([.. cachePaths.Select(p => (JsonNode)p)]),
            },
        };
        if (allowedDomains is not null) { sandbox["network"] = new JsonObject { ["allowedDomains"] = new JsonArray([.. allowedDomains.Select(p => (JsonNode)p)]) }; }
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
        entries.AddRange(CachePaths.Select(p => (p, "write")));
        var args = new List<string>
        {
            "-c", "permissions." + CodexProfile + ".filesystem={" + string.Join(",", entries.Select(e => TomlString(e.Path) + "=" + TomlString(e.Access))) + "}",
            "-c", "permissions." + CodexProfile + ".network.enabled=" + (Network ? "true" : "false"),
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
