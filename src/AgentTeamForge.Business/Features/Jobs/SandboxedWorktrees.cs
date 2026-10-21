using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Files;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>
/// Linked worktrees a sandboxed agent can write into (ADR 0013, "Git metadata attacks"). The agent may rewrite its worktree's admin
/// dir (<c>commondir</c>, <c>config.worktree</c>) and the <c>.git</c> file. ATF therefore records, before the agent runs, the admin
/// dir and common dir of every such worktree, in a daemon-private file that survives a restart. Every later daemon git run there
/// (1) validates the metadata against the record, (2) refuses while an agent of the worktree is alive, (3) is pinned to the
/// recorded dirs through <c>GIT_DIR</c>/<c>GIT_COMMON_DIR</c> so it never reads the mutable pointers, and (4) runs without hooks,
/// fsmonitor, a credential helper (except network commands) or any filter, diff or merge driver that could run workspace code.
/// </summary>
public static class SandboxedWorktrees
{
    public const string TamperedReason = "sandbox_worktree_tampered";

    /// <summary>A directory with an empty common dir is a validated "no git layout here" baseline: any <c>.git</c> that appears later is a mismatch.</summary>
    sealed record Entry(string Root, string Common, string Admin)
    {
        public bool NoGit => Common.Length == 0;
    }

    static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, int> Live = new(StringComparer.Ordinal);
    static readonly Lock Gate = new();
    static string? _registryFile;

    static readonly string[] HardeningArguments = ["-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=", "-c", "diff.external=", "-c", "core.pager=cat"];
    static readonly string[] CredentialHardening = ["-c", "credential.helper="];
    static readonly HashSet<string> NetworkCommands = ["push", "fetch", "pull", "clone", "ls-remote", "remote"];
    static readonly TimeSpan ConfigTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Points the registry at the daemon's state dir and loads what an earlier daemon recorded.</summary>
    public static void Configure(string stateDirectory)
    {
        lock (Gate)
        {
            _registryFile = Path.Combine(stateDirectory, "sandbox", "worktrees.json");
            LoadFrom(_registryFile);
        }
    }

    internal static void LoadFrom(string file)
    {
        if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonArray recorded) { return; }
        foreach (var item in recorded.OfType<JsonObject>())
        {
            if ((string?)item["root"] is { } root && (string?)item["common"] is { } common && (string?)item["admin"] is { } admin)
            {
                Entries.TryAdd(root, new(root, common, admin));
            }
        }
    }

    internal static void Forget(string worktreePath) => Entries.TryRemove(Canonical(worktreePath), out _);

    /// <summary>Registers a worktree ATF just created, replacing any earlier record of the path.</summary>
    public static bool RegisterCreated(string? worktreePath, string? origin) => RegisterFromOrigin(worktreePath, origin, replace: true);

    /// <summary>Registers a job's worktree against the common dir of its origin checkout, which the sandbox cannot reach.</summary>
    public static bool RegisterJob(JobRecord job) => RegisterFromOrigin(job.WorktreePath, job.Cwd, replace: false);

    public static bool RegisterFromOrigin(string? worktreePath, string? origin, bool replace = false)
    {
        if (worktreePath is null || origin is null || GitLayout.Find(origin) is not { } layout || !File.Exists(Path.Combine(worktreePath, ".git"))) { return false; }
        var admin = GitLayout.Find(worktreePath)?.GitDir;
        return admin is not null && Add(worktreePath, layout.CommonDir, admin, replace);
    }

    /// <summary>
    /// Binds a sandboxed launch to its trusted git layout and returns why it cannot, or null. A directory already inside a recorded
    /// worktree must discover exactly the recorded root, admin dir and common dir: a nested <c>.git</c> the agent planted (with its own
    /// <c>commondir</c>) would otherwise point the sandbox's git allowances at another repository. Only a directory with no record yet is
    /// recorded, from the layout found on disk before any agent of ATF's has run there; the first record wins.
    /// </summary>
    public static string? BindLaunch(string launchDirectory, GitLayout? layout)
    {
        if (Find(launchDirectory) is { } entry)
        {
            if (entry.NoGit) { return layout is null ? null : "git_layout_appeared"; }
            return layout is not null && Canonical(layout.Root) == entry.Root && Canonical(layout.GitDir) == entry.Admin && Canonical(layout.CommonDir) == entry.Common
                ? null : "nested_git_metadata";
        }
        // Without a layout the baseline "no git here" is recorded, so a layout the agent creates later is a mismatch, not a new trusted record.
        if (layout is not null) { Add(layout.Root, layout.CommonDir, layout.GitDir, replace: false); }
        else { Entries.TryAdd(Canonical(launchDirectory), new Entry(Canonical(launchDirectory), "", "")); Persist(); }
        return null;
    }

    static bool Add(string root, string common, string admin, bool replace)
    {
        var entry = new Entry(Canonical(root), Canonical(common), Canonical(admin));
        if (replace) { Entries[entry.Root] = entry; }
        else if (!Entries.TryAdd(entry.Root, entry)) { return true; }
        Persist();
        return true;
    }

    static void Persist()
    {
        lock (Gate)
        {
            if (_registryFile is not null) { SaveTo(_registryFile); }
        }
    }

    internal static void SaveTo(string file)
    {
        var all = new JsonArray([.. Entries.Values.Select(e => (JsonNode)new JsonObject { ["root"] = e.Root, ["common"] = e.Common, ["admin"] = e.Admin })]);
        PrivateFiles.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp";
        using (var stream = new FileStream(temporary, PrivateFiles.Options(FileMode.Create, FileAccess.Write)))
        using (var writer = new StreamWriter(stream)) { writer.Write(all.ToJsonString()); }
        File.Move(temporary, file, overwrite: true);
    }

    /// <summary>Registers the worktree of every sandboxed job in the store that an older daemon did not record.</summary>
    public static void Restore(JobStore store)
    {
        foreach (var (path, cwd) in store.SandboxedWorktrees()) { RegisterFromOrigin(path, cwd); }
    }

    public static void Clear()
    {
        Entries.Clear();
        Live.Clear();
    }

    /// <summary>An agent of the worktree containing <paramref name="directory"/> is running; daemon git refuses there until it ends.</summary>
    public static string? MarkLive(string directory)
    {
        if (Find(directory) is not { } entry) { return null; }
        Live.AddOrUpdate(entry.Root, 1, (_, count) => count + 1);
        return entry.Root;
    }

    public static void ClearLive(string root) => Live.AddOrUpdate(root, 0, (_, count) => Math.Max(0, count - 1));

    static Entry? Find(string directory)
    {
        var start = Canonical(directory);
        for (var current = start; current is not null; current = Path.GetDirectoryName(current))
        {
            // A no-git baseline describes exactly its own directory, not whatever lies beneath it.
            if (Entries.TryGetValue(current, out var entry) && (!entry.NoGit || current == start)) { return entry; }
        }
        return null;
    }

    /// <summary>Null when <paramref name="directory"/> is not inside a registered worktree or its metadata is as recorded, else a short reason.</summary>
    public static string? Validate(string directory) => Find(directory) is { } entry ? Validate(entry) : null;

    /// <summary>Whether a registered worktree failed validation (the job fails with <see cref="TamperedReason"/>).</summary>
    public static bool Tampered(string directory) => Validate(directory) is not null;

    static string? Validate(Entry entry)
    {
        try
        {
            var marker = Path.Combine(entry.Root, ".git");
            if (entry.NoGit) { return File.Exists(marker) || Directory.Exists(marker) || IsLink(marker) ? "git_layout_appeared" : null; }
            if (IsLink(marker)) { return "git_is_link"; }
            // A plain checkout (admin dir is the common dir) has a .git directory that must still be the recorded one.
            if (entry.Admin == entry.Common) { return Directory.Exists(marker) && Canonical(marker) == entry.Common ? null : "git_changed"; }
            if (!File.Exists(marker)) { return Directory.Exists(marker) ? "git_not_a_file" : "git_missing"; }
            var line = File.ReadLines(marker).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
            if (line is null) { return "git_file_invalid"; }
            var admin = Path.GetFullPath(line["gitdir:".Length..].Trim(), entry.Root);
            if (IsLink(admin) || !Directory.Exists(admin)) { return "admin_dir_invalid"; }
            if (Canonical(admin) != entry.Admin) { return "admin_dir_moved"; }
            var pointer = Path.Combine(admin, "commondir");
            if (!File.Exists(pointer) || IsLink(pointer) || Canonical(GitLayout.ResolveCommon(admin)) != entry.Common) { return "common_dir_changed"; }
            if (File.Exists(Path.Combine(admin, "config.worktree"))) { return "config_worktree_present"; }
            return Directory.EnumerateFileSystemEntries(admin).Any(IsLink) ? "symlink_in_git_dir" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "git_unreadable";
        }
    }

    /// <summary>
    /// The working directory for a sandboxed job, resolved through symlinks and required to stay inside the worktree. A sandboxed agent can
    /// replace a subdirectory with a link, and the sandbox grants the cwd as its writable workspace. Null when it escapes.
    /// </summary>
    public static string? ContainedWorkingDirectory(string worktreeRoot, string? workingDirectory)
    {
        if (workingDirectory is null) { return null; }
        var root = Canonical(worktreeRoot);
        var resolved = PhysicalPath.Resolve(Path.GetFullPath(workingDirectory));
        if (resolved is null) { return null; }
        resolved = Path.TrimEndingDirectorySeparator(resolved);
        return SandboxLaunch.Contains(root, resolved) ? resolved : null;
    }

    static bool IsLink(string path) => (File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    static string Canonical(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return PhysicalPath.Resolve(full) is { } physical ? Path.TrimEndingDirectorySeparator(physical) : full;
    }

    /// <summary>
    /// Pins and hardens a git process that is about to run in <paramref name="cwd"/>; a no-op outside a registered worktree. Call it before
    /// adding any other argument. Throws <see cref="TamperedWorktreeException"/> when the worktree no longer validates or an agent still runs there.
    /// </summary>
    public static void ApplyGuard(ProcessStartInfo info, string cwd, string? command)
    {
        if (Find(cwd) is not { } entry) { return; }
        if (Validate(entry) is { } reason) { throw new TamperedWorktreeException(entry.Root, reason); }
        if (entry.NoGit) { return; }
        if (Live.TryGetValue(entry.Root, out var live) && live > 0) { throw new TamperedWorktreeException(entry.Root, "agent_still_running"); }
        Pin(info, entry);
        foreach (var argument in HardeningArguments) { info.ArgumentList.Add(argument); }
        if (command is null || !NetworkCommands.Contains(command)) { foreach (var argument in CredentialHardening) { info.ArgumentList.Add(argument); } }
        foreach (var argument in DriverOverrides(entry)) { info.ArgumentList.Add(argument); }
    }

    static void Pin(ProcessStartInfo info, Entry entry)
    {
        // The recorded dirs replace the worktree's own pointers, so a swap after validation is never read.
        info.Environment["GIT_DIR"] = entry.Admin;
        info.Environment["GIT_COMMON_DIR"] = entry.Common;
        info.Environment["GIT_WORK_TREE"] = entry.Root;
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
    }

    /// <summary>
    /// Blanks every filter, diff and merge driver that any config file the git run could read defines, except the LFS filter when it is
    /// the real git-lfs. Computed for every invocation from the pinned config and never cached. Conditional includes (<c>includeIf</c>) are
    /// neutralised by reading every included file whatever its condition and blanking by name: a driver that a branch switch would switch
    /// on is already blank. An include file inside a writable root is refused. The repo config is read-only to the agent, but a driver
    /// can name a script the agent edits.
    /// </summary>
    static string[] DriverOverrides(Entry entry)
    {
        string[] roots = [entry.Root, entry.Admin, entry.Common];
        var items = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<(string Origin, string Path, int Depth)>();
        Collect(entry, ["config", "--list", "--no-includes", "--show-origin", "-z"], items, pending, visited, roots, 0);
        while (pending.Count > 0)
        {
            var (_, file, depth) = pending.Dequeue();
            if (depth > 10) { throw new TamperedWorktreeException(entry.Root, "config_include_depth"); }
            Collect(entry, ["config", "--file", file, "--list", "--no-includes", "--show-origin", "-z"], items, pending, visited, roots, depth + 1);
        }
        return Overrides(string.Join('\0', items), roots, Environment.GetEnvironmentVariable("PATH"));
    }

    static void Collect(Entry entry, string[] arguments, List<string> items, Queue<(string, string, int)> pending, HashSet<string> visited, string[] roots, int depth)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        Pin(info, entry);
        foreach (var argument in arguments) { info.ArgumentList.Add(argument); }
        string listing;
        try
        {
            using var process = Process.Start(info) ?? throw new TamperedWorktreeException(entry.Root, "git_config_unreadable");
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ConfigTimeout)) { process.Kill(); throw new TamperedWorktreeException(entry.Root, "git_config_unreadable"); }
            listing = output.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            throw new TamperedWorktreeException(entry.Root, "git_config_unreadable");
        }
        // With --show-origin and -z each entry is "origin\0key\nvalue\0".
        var fields = listing.Split('\0');
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            var origin = fields[i];
            var item = fields[i + 1];
            items.Add(item);
            var split = item.IndexOf('\n');
            var key = (split < 0 ? item : item[..split]).ToLowerInvariant();
            if (key != "include.path" && !(key.StartsWith("includeif.", StringComparison.Ordinal) && key.EndsWith(".path", StringComparison.Ordinal))) { continue; }
            var value = split < 0 ? "" : item[(split + 1)..];
            var baseDirectory = origin.StartsWith("file:", StringComparison.Ordinal) ? Path.GetDirectoryName(origin[5..]) : null;
            var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = value.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, value[2..]) : baseDirectory is null ? null : Path.GetFullPath(value, baseDirectory);
            if (path is null) { throw new TamperedWorktreeException(entry.Root, "config_include_unresolvable"); }
            if (roots.Any(r => Within(r, PhysicalPath.Resolve(path) ?? path))) { throw new TamperedWorktreeException(entry.Root, "config_include_in_workspace"); }
            if (File.Exists(path) && visited.Add(path)) { pending.Enqueue((origin, path, depth)); }
        }
    }

    static readonly (string Variable, string Command)[] LfsGrammar = [("clean", "clean -- %f"), ("smudge", "smudge -- %f"), ("process", "filter-process")];

    internal static string[] Overrides(string configList, string[] writableRoots, string? path)
    {
        // Every definition of every variable is kept: the listing is a discovery of all included files, not the effective config, so a
        // safe-looking definition in an inactive include must never hide an unsafe active one.
        var filters = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        var overrides = new List<string>();
        foreach (var item in configList.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = item.IndexOf('\n');
            var key = split < 0 ? item : item[..split];
            var value = split < 0 ? "" : item[(split + 1)..];
            var parts = key.Split('.');
            if (parts.Length < 3) { continue; }
            var name = string.Join('.', parts[1..^1]);
            var variable = parts[^1].ToLowerInvariant();
            switch (parts[0].ToLowerInvariant(), variable)
            {
                case ("filter", "clean" or "smudge" or "process" or "required"):
                    if (!filters.TryGetValue(name, out var values)) { filters[name] = values = new(StringComparer.Ordinal); }
                    if (!values.TryGetValue(variable, out var definitions)) { values[variable] = definitions = []; }
                    definitions.Add(value);
                    break;
                case ("diff", "textconv" or "command"):
                    overrides.AddRange(["-c", $"diff.{name}.{variable}="]);
                    break;
                case ("merge", "driver"):
                    overrides.AddRange(["-c", $"merge.{name}.driver="]);
                    break;
            }
        }
        foreach (var (name, values) in filters)
        {
            if (name == "lfs" && PinLfs(values, writableRoots, path) is { } pinned) { overrides.AddRange(pinned); continue; }
            foreach (var variable in new[] { "clean", "smudge", "process" }) { overrides.AddRange(["-c", $"filter.{name}.{variable}="]); }
            overrides.AddRange(["-c", $"filter.{name}.required=false"]);
        }
        return [.. overrides];
    }

    /// <summary>
    /// The LFS filter is kept only when every definition of every command it has, in any config file, is exactly git-lfs's own
    /// (<c>git-lfs clean -- %f</c> and the like) and its executable resolves, through links, to an absolute path outside every writable
    /// root. The commands are then pinned to that resolved path, so the shell never searches <c>PATH</c> again. Otherwise null: blank it.
    /// </summary>
    static string[]? PinLfs(Dictionary<string, List<string>> commands, string[] writableRoots, string? path)
    {
        var pinned = new List<string>();
        foreach (var (variable, grammar) in LfsGrammar)
        {
            if (!commands.TryGetValue(variable, out var definitions)) { continue; }
            string? real = null;
            foreach (var command in definitions)
            {
                var space = command.IndexOf(' ');
                if (space < 0 || command[(space + 1)..] != grammar || ResolveLfs(command[..space], writableRoots, path) is not { } resolved) { return null; }
                real = resolved;
            }
            pinned.AddRange(["-c", $"filter.lfs.{variable}={real} {grammar}"]);
        }
        return pinned.Count > 0 ? [.. pinned] : null;
    }

    static bool SafePath(string value) => value.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '+');

    /// <summary>
    /// The real path of the executable the shell would run for <paramref name="token"/>, or null when it is not provably outside every
    /// writable root. A bare <c>git-lfs</c> is searched on an all-absolute <c>PATH</c> with executable-access semantics: a non-executable
    /// file is skipped as the shell skips it, and a <c>PATH</c> entry inside a writable root fails the whole resolution.
    /// </summary>
    static string? ResolveLfs(string token, string[] writableRoots, string? path)
    {
        string? candidate = null;
        if (Path.IsPathRooted(token)) { candidate = token; }
        else if (token == "git-lfs")
        {
            foreach (var directory in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                // A relative PATH entry means the current directory, which is the workspace.
                if (!Path.IsPathRooted(directory) || writableRoots.Any(r => Within(r, PhysicalPath.Resolve(directory) ?? directory))) { return null; }
                var probe = Path.Combine(directory, token);
                if (!File.Exists(probe)) { continue; }
                if (PhysicalPath.Resolve(probe) is not { } realProbe || writableRoots.Any(r => Within(r, realProbe))) { return null; }
                if (IsExecutable(probe)) { candidate = probe; break; }
            }
        }
        if (candidate is null || Path.GetFileName(candidate) != "git-lfs" || !File.Exists(candidate)) { return null; }
        var real = PhysicalPath.Resolve(candidate);
        return real is not null && Path.IsPathRooted(real) && SafePath(real) && !writableRoots.Any(r => Within(r, real)) ? real : null;
    }

    static bool IsExecutable(string file) =>
        OperatingSystem.IsWindows() || (File.GetUnixFileMode(file) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    static bool Within(string root, string path) => SandboxLaunch.Contains(root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, root)));
}

public sealed class TamperedWorktreeException(string root, string reason) : Exception($"sandboxed worktree {root} refused daemon git: {reason}");
