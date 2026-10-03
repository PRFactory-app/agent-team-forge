using System.Text;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Business.Features.Jobs;

public sealed record WorktreeCleanupResult(string Path, string? JobId, string Outcome, string? Reason = null, IReadOnlyList<string>? Details = null);

/// <summary>Removes a finished job worktree only when nothing of value would be lost. Never deletes directories itself.</summary>
public sealed class WorktreeCleanup(JobStore store, BackendCatalog backends)
{
    internal Action<string, string, string>? BeforeBranchDelete { get; set; }
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    static readonly string[] Disposable =
    [
        "bin", "obj", "TestResults", "artifacts", "evidence", "node_modules", ".vs", "__pycache__", ".pytest_cache", ".venv", ".next", ".turbo",
    ];

    public string Root => store.WorktreeRoot;

    public IReadOnlyList<string> ListWorktrees() => Directory.Exists(Root)
        ? Directory.GetDirectories(Root).Where(d => Path.GetFileName(d).StartsWith("job_", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray()
        : [];

    /// <summary>One job's worktree by id; a directory under the root with no job row is still checked like any other.</summary>
    public async Task<(WorktreeCleanupResult? Result, string? Error)> RemoveJobAsync(string jobId, bool force, bool dryRun, CancellationToken ct)
    {
        var job = jobId.Length is 0 or > 64 ? null : store.GetJob(jobId);
        return job is { WorktreePath: null } ? (null, JobErrors.NoWorktree)
            : await RemoveExistingAsync(job?.WorktreePath ?? Path.Combine(Root, jobId), force, dryRun, ct);
    }

    /// <summary>not_found for a worktree that is gone (or never existed), rather than reporting it as not owned.</summary>
    internal async Task<(WorktreeCleanupResult? Result, string? Error)> RemoveExistingAsync(string path, bool force, bool dryRun, CancellationToken ct) =>
        Directory.Exists(path) ? (await RemoveAsync(path, force, dryRun, auto: false, ct), null) : (null, JobErrors.NotFound);

    public async Task<WorktreeCleanupResult> RemoveAsync(string path, bool force, bool dryRun, bool auto, CancellationToken ct)
    {
        path = Path.GetFullPath(path);
        var id = Path.GetFileName(path);
        WorktreeCleanupResult Kept(string reason, IReadOnlyList<string>? details = null) => new(path, id, "kept", reason, details);

        // Containment is lexical on the unresolved path and the final component must not be a link.
        // Git reports the physical toplevel (macOS /tmp is /private/tmp), so compare it physically.
        if (Path.GetDirectoryName(path) != Path.GetFullPath(Root) || !id.StartsWith("job_", StringComparison.Ordinal)
            || !Directory.Exists(path) || new DirectoryInfo(path).LinkTarget is not null
            || await JobWorktree.GitAsync(path, Timeout, ct, "rev-parse", "--show-toplevel") is not { Length: > 0 } toplevel
            || PhysicalPath.Resolve(path) is not { } physical || Path.GetFullPath(toplevel) != physical)
        {
            return Kept("not_owned_path");
        }

        var jobs = store.GetJobsByWorktreePath(path);
        foreach (var job in jobs)
        {
            if (job.Status is JobStatus.Queued or JobStatus.Running or JobStatus.NeedsReconciliation || store.IsSessionFenced(job.JobId))
            {
                return Kept("job_active");
            }
        }
        foreach (var job in jobs)
        {
            if (AgentLive(job)) { return Kept("agent_live", ["stop_agent first"]); }
        }

        var status = await JobWorktree.GitAsync(path, Timeout, ct, "status", "--porcelain", "--untracked-files=all");
        if (status is null) { return Kept("git_unknown"); }
        if (status.Length != 0 && !force) { return Kept("dirty", [.. Lines(status).Take(20)]); }

        var ignored = await NonDisposableIgnoredAsync(path, ct);
        if (ignored is null) { return Kept("git_unknown"); }
        if (ignored.Count != 0 && !force) { return Kept("ignored_files", [.. ignored.Take(20)]); }

        var branch = $"refs/heads/atf/job-{id}";
        var tip = await JobWorktree.GitAsync(path, Timeout, ct, "rev-parse", "--verify", "--quiet", branch);
        var head = await JobWorktree.GitAsync(path, Timeout, ct, "rev-parse", "--verify", "HEAD");
        if (head is null) { return Kept("git_unknown"); }
        foreach (var sha in new[] { head, tip }.Where(s => !string.IsNullOrEmpty(s)).Distinct())
        {
            var reason = await Unmerged(path, sha!, auto, ct);
            if (reason is not null) { return Kept(reason); }
        }

        var common = await JobWorktree.GitAsync(path, Timeout, ct, "rev-parse", "--path-format=absolute", "--git-common-dir");
        if (string.IsNullOrEmpty(common)) { return Kept("git_unknown"); }
        var repo = Path.GetFileName(common) == ".git" ? Path.GetDirectoryName(common)! : common;
        if (dryRun) { return new(path, id, "would_remove"); }

        // The same per-repository lock as worktree add: a removal must not delete admin files an add is reading.
        IDisposable held;
        try
        {
            held = await WorktreeLock.AcquireAsync(common, ct);
        }
        catch (TimeoutException ex)
        {
            return Kept("worktree_locked", [ex.Message]);
        }
        using (held)
        {
            var args = force ? new[] { "worktree", "remove", "--force", "--", path } : ["worktree", "remove", "--", path];
            var removal = await JobWorktree.RunAsync(repo, TimeSpan.FromMinutes(5), ct, args);
            if (removal is not { ExitCode: 0 })
            {
                return Kept("git_refused", removal is not null && JobWorktree.ErrorText(removal) is { } error ? [.. Lines(error).Take(20)] : null);
            }

            if (!string.IsNullOrEmpty(tip))
            {
                BeforeBranchDelete?.Invoke(repo, branch, tip);
                var list = await JobWorktree.GitAsync(repo, Timeout, ct, "worktree", "list", "--porcelain");
                if (list is not null && !list.Split('\n').Any(l => l.Trim() == $"branch {branch}"))
                {
                    await JobWorktree.GitAsync(repo, Timeout, ct, "update-ref", "-d", branch, tip);
                }
            }
        }
        return new(path, id, "removed");
    }

    bool AgentLive(JobRecord job)
    {
        try
        {
            var backend = backends.Resolve(job.Backend);
            if (backend is HerdrInteractiveBackend herdr) { return herdr.HasOwnedJobs(store.GetSessionJobs(job.JobId)); }
            return job.SessionId is { } session && backend is IInteractiveSessionStop stop && stop.HasIdleSession(session);
        }
        catch (Exception ex) when (ex is HerdrLaunchException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    static async Task<string?> Unmerged(string path, string sha, bool auto, CancellationToken ct)
    {
        if (auto)
        {
            var target = await DefaultBranch(path, ct);
            return target is not null && await JobWorktree.GitAsync(path, Timeout, ct, "merge-base", "--is-ancestor", sha, target) is not null
                ? null : "not_merged";
        }
        var refs = await JobWorktree.GitAsync(path, Timeout, ct, "for-each-ref", "--contains", sha, "--format=%(refname)", "refs/remotes", "refs/tags", "refs/heads");
        return refs is not null && Lines(refs).Any(r => !r.StartsWith("refs/heads/atf/job-", StringComparison.Ordinal)) ? null : "unmerged_commits";
    }

    static async Task<string?> DefaultBranch(string path, CancellationToken ct)
    {
        var symbolic = await JobWorktree.GitAsync(path, Timeout, ct, "symbolic-ref", "--quiet", "refs/remotes/origin/HEAD");
        if (!string.IsNullOrEmpty(symbolic)) { return symbolic; }
        foreach (var candidate in new[] { "refs/remotes/origin/main", "refs/remotes/origin/master", "refs/heads/main", "refs/heads/master" })
        {
            if (!string.IsNullOrEmpty(await JobWorktree.GitAsync(path, Timeout, ct, "rev-parse", "--verify", "--quiet", candidate))) { return candidate; }
        }
        return null;
    }

    static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Ignored entries that are not disposable build output; null when git could not answer.</summary>
    public static async Task<IReadOnlyList<string>?> NonDisposableIgnoredAsync(string path, CancellationToken ct)
    {
        var output = await JobWorktree.GitRawAsync(path, Timeout, ct, "status", "--porcelain", "--ignored=matching", "-z");
        if (output is null || Encoding.UTF8.GetByteCount(output) >= JobWorktree.OutputCap) { return null; }
        var found = new List<string>();
        foreach (var entry in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!entry.StartsWith("!! ", StringComparison.Ordinal)) { continue; }
            var name = entry[3..];
            // Root-level SDK link that workers create to the main checkout's .tools; unlinking it loses nothing.
            if (name == ".tools" && new FileInfo(Path.Combine(path, name)).LinkTarget is not null) { continue; }
            var last = name.TrimEnd('/').Split('/')[^1];
            if (!Disposable.Contains(last, StringComparer.Ordinal)) { found.Add(name); }
        }
        return found;
    }

    public static IReadOnlyList<string>? NonDisposableIgnored(string path) =>
        NonDisposableIgnoredAsync(path, CancellationToken.None).GetAwaiter().GetResult();
}

/// <summary>Owned-job entry point for the remove_worktree tool.</summary>
public sealed class RemoveWorktree(JobStore store, BoundPrincipal principal, WorktreeCleanup cleanup)
{
    public async Task<(WorktreeCleanupResult? Result, string? Error)> ExecuteAsync(string jobId, bool force, bool dryRun, CancellationToken ct)
    {
        var job = string.IsNullOrWhiteSpace(jobId) || jobId.Length > 64 ? null : store.GetJob(jobId);
        if (job is null || job.Principal != principal.Principal || job.Team != principal.Team) { return (null, JobErrors.NotFound); }
        return job.WorktreePath is null ? (null, JobErrors.NoWorktree) : await cleanup.RemoveExistingAsync(job.WorktreePath, force, dryRun, ct);
    }
}
