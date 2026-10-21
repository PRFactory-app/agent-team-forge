using System.Collections.Concurrent;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>
/// Worktrees a sandboxed agent can write into (ADR 0013, "Git metadata attacks"). The agent may rewrite its worktree's admin dir
/// (<c>commondir</c>, <c>config.worktree</c>) and the <c>.git</c> file, so every later daemon git run there is checked against the
/// common dir recorded from the origin checkout, and runs without hooks, fsmonitor or a credential helper.
/// </summary>
public static class SandboxedWorktrees
{
    const string Failed = "sandbox_worktree_tampered";

    static readonly ConcurrentDictionary<string, string> Expected = new(StringComparer.Ordinal);

    internal static readonly string[] HardeningArguments = ["-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=", "-c", "diff.external=", "-c", "core.pager=cat"];

    /// <summary>Network commands keep the operator's configured credential helper; ATF's own credential path replaces it later (A4).</summary>
    internal static readonly string[] CredentialHardening = ["-c", "credential.helper="];

    static readonly HashSet<string> NetworkCommands = ["push", "fetch", "pull", "clone", "ls-remote", "remote"];

    public const string TamperedReason = Failed;

    /// <summary>Records a worktree and the common dir it must keep pointing at. The origin checkout is outside the sandbox's reach.</summary>
    public static void Register(string worktreePath, string expectedCommonDir) => Expected[Canonical(worktreePath)] = Canonical(expectedCommonDir);

    /// <summary>Registers the worktree of a job whose effective mode is sandboxed. False when its origin checkout has no readable git dir.</summary>
    public static bool RegisterJob(JobRecord job) => RegisterFromOrigin(job.WorktreePath, job.Cwd);

    public static bool RegisterFromOrigin(string? worktreePath, string? origin)
    {
        if (worktreePath is null || origin is null || GitLayout.Find(origin) is not { } layout) { return false; }
        Register(worktreePath, layout.CommonDir);
        return true;
    }

    /// <summary>Registers every sandboxed job's worktree from the durable store, so a restarted daemon hardens them before any launch.</summary>
    public static void Restore(JobStore store)
    {
        foreach (var (path, cwd) in store.SandboxedWorktrees()) { RegisterFromOrigin(path, cwd); }
    }

    public static void Clear() => Expected.Clear();

    /// <summary>Null when <paramref name="directory"/> is not inside a registered worktree.</summary>
    internal static (string Root, string Common)? Find(string directory)
    {
        for (var current = Canonical(directory); current is not null; current = Path.GetDirectoryName(current))
        {
            if (Expected.TryGetValue(current, out var common)) { return (current, common); }
        }
        return null;
    }

    /// <summary>Null when the worktree's git metadata is as ATF recorded it, else a short reason.</summary>
    public static string? Validate(string directory) => Find(directory) is { } found ? Validate(found.Root, found.Common) : null;

    /// <summary>Whether a sandboxed worktree failed validation (the job fails with <see cref="TamperedReason"/>).</summary>
    public static bool Tampered(string directory) => Validate(directory) is not null;

    internal static string? Validate(string root, string expectedCommon)
    {
        try
        {
            var marker = Path.Combine(root, ".git");
            if (IsLink(marker)) { return "git_is_link"; }
            if (Directory.Exists(marker)) { return Canonical(marker) == expectedCommon ? CheckEntries(marker) : "common_dir_changed"; }
            if (!File.Exists(marker)) { return "git_missing"; }
            var line = File.ReadLines(marker).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
            if (line is null) { return "git_file_invalid"; }
            var admin = Path.GetFullPath(line["gitdir:".Length..].Trim(), root);
            if (IsLink(admin) || !Directory.Exists(admin)) { return "admin_dir_invalid"; }
            if (Canonical(Path.GetDirectoryName(admin)!) != Path.Combine(expectedCommon, "worktrees")) { return "admin_dir_moved"; }
            var pointer = Path.Combine(admin, "commondir");
            if (!File.Exists(pointer) || IsLink(pointer) || Canonical(GitLayout.ResolveCommon(admin)) != expectedCommon) { return "common_dir_changed"; }
            if (File.Exists(Path.Combine(admin, "config.worktree"))) { return "config_worktree_present"; }
            return CheckEntries(admin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "git_unreadable";
        }
    }

    static string? CheckEntries(string directory) =>
        Directory.EnumerateFileSystemEntries(directory).Any(IsLink) ? "symlink_in_git_dir" : null;

    static bool IsLink(string path) => (File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    static string Canonical(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return PhysicalPath.Resolve(full) is { } physical ? Path.TrimEndingDirectorySeparator(physical) : full;
    }

    /// <summary>The leading <c>-c</c> overrides for a daemon git run, or null when the directory is not a sandboxed worktree. Throws when it is one that no longer validates.</summary>
    internal static IReadOnlyList<string>? Guard(string directory, string? command)
    {
        if (Find(directory) is not { } found) { return null; }
        if (Validate(found.Root, found.Common) is { } reason) { throw new TamperedWorktreeException(found.Root, reason); }
        return command is not null && NetworkCommands.Contains(command) ? HardeningArguments : [.. HardeningArguments, .. CredentialHardening];
    }
}

public sealed class TamperedWorktreeException(string root, string reason) : Exception($"sandboxed worktree {root} failed git metadata validation: {reason}");
