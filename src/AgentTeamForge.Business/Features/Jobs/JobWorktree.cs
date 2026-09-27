using AgentTeamForge.DAL.Files;
using System.Diagnostics;
using System.Text;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Git worktrees are created by the daemon from a pinned submit-time HEAD.</summary>
public static class JobWorktree
{
    static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    // Checkout of a large repository (and LFS/post-checkout hooks) can take minutes.
    static readonly TimeSpan AddTimeout = TimeSpan.FromMinutes(10);
    const int MaxGitOutputBytes = 1024 * 1024;

    // Git's own output is already in the pipe when it exits; a hook descendant may keep it open.
    static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(1);

    public static string? Head(string cwd)
    {
        var root = Git(cwd, QueryTimeout, "rev-parse", "--show-toplevel");
        return root is null ? null : Git(cwd, QueryTimeout, "rev-parse", "--verify", "HEAD");
    }

    public static string? Branch(string cwd) => Git(cwd, QueryTimeout, "symbolic-ref", "--quiet", "--short", "HEAD");

    public static bool IsCommitSha(string value) => (value.Length is 40 or 64) && value.All(Uri.IsHexDigit);

    /// <summary>Prepare from the persisted SHA, never resolve a moving branch on recovery.</summary>
    public static bool Prepare(string repository, string path, string branch, string startingSha)
    {
        if (!IsCommitSha(startingSha)) { return false; }
        if (Directory.Exists(path))
        {
            return Git(path, QueryTimeout, "rev-parse", "--show-prefix") == string.Empty &&
                Branch(path) == branch &&
                Git(path, QueryTimeout, "merge-base", "--is-ancestor", startingSha, "HEAD") is not null &&
                Git(path, QueryTimeout, "rev-parse", "--path-format=absolute", "--git-common-dir") ==
                Git(repository, QueryTimeout, "rev-parse", "--path-format=absolute", "--git-common-dir");
        }
        PrivateFiles.CreateDirectory(Path.GetDirectoryName(path)!);
        // An interrupted worktree add can have already created the branch.
        var existing = Git(repository, QueryTimeout, "rev-parse", "--verify", $"refs/heads/{branch}");
        if (existing is not null)
        {
            return existing == startingSha && Git(repository, AddTimeout, "worktree", "add", path, branch) is not null;
        }
        return Git(repository, AddTimeout, "worktree", "add", "-b", branch, path, startingSha) is not null;
    }

    public static bool Prepare(JobRecord job)
    {
        if (job.WorktreePath is null)
        {
            return true;
        }

        if (Directory.Exists(job.WorktreePath))
        {
            // A follow-up and a restarted daemon must preserve the existing checkout.
            // The agent may have switched branches there; only require it to still be a worktree root.
            return job.WorktreeBase is not null && IsCommitSha(job.WorktreeBase) &&
                Git(job.WorktreePath, QueryTimeout, "rev-parse", "--show-prefix") == string.Empty &&
                Git(job.WorktreePath, QueryTimeout, "merge-base", "--is-ancestor", job.WorktreeBase, "HEAD") is not null;
        }

        if (job.ParentJobId is not null || job.Cwd is null || job.WorktreeBranch is null || job.WorktreeBase is null)
        {
            return false;
        }

        try
        {
            var parent = Path.GetDirectoryName(job.WorktreePath)!;
            PrivateFiles.CreateDirectory(parent);

            return Prepare(job.Cwd, job.WorktreePath, job.WorktreeBranch, job.WorktreeBase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The submitted cwd's counterpart inside the worktree, so a repo subdirectory stays the working directory.</summary>
    public static string? WorkingDirectory(JobRecord job)
    {
        if (job.WorktreePath is null || job.Cwd is null)
        {
            return job.WorktreePath ?? job.Cwd;
        }

        var prefix = Git(job.Cwd, QueryTimeout, "rev-parse", "--show-prefix");
        var mapped = string.IsNullOrEmpty(prefix) ? job.WorktreePath : Path.Combine(job.WorktreePath, prefix);
        return Directory.Exists(mapped) ? mapped : job.WorktreePath;
    }

    /// <summary>Trimmed stdout (possibly empty) on exit code 0; otherwise null.</summary>
    static string? Git(string cwd, TimeSpan timeout, params string[] args) =>
        GitAsync(cwd, timeout, CancellationToken.None, args).GetAwaiter().GetResult();

    internal static async Task<string?> GitAsync(string cwd, TimeSpan timeout, CancellationToken cancellationToken, params string[] args)
        => await GitOutputAsync(cwd, timeout, false, cancellationToken, args);

    internal static async Task<string?> GitOutputAsync(string cwd, TimeSpan timeout, bool includeFailure, CancellationToken cancellationToken, params string[] args)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var info = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
            };
            info.Environment["GIT_TERMINAL_PROMPT"] = "0";
            info.ArgumentList.Add("-C");
            info.ArgumentList.Add(cwd);
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            // A hook descendant may retain stdout after git exits. Exit shares the whole
            // deadline; after exit the drain gets a short grace and keeps what it read.
            // Output beyond the cap is discarded while draining continues.
            using var drain = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var output = DrainAsync(process.StandardOutput.BaseStream, drain.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Git exited meanwhile.
                }

                drain.Cancel();
                await output;
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            drain.CancelAfter(DrainGrace);
            var text = await output;
            cancellationToken.ThrowIfCancellationRequested();
            return process.ExitCode == 0 || includeFailure ? text.Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    static async Task<string> DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var kept = new MemoryStream();
        var buffer = new byte[16 * 1024];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                var room = (int)Math.Min(n, MaxGitOutputBytes - kept.Length);
                kept.Write(buffer, 0, room);
            }
        }
        catch (OperationCanceledException)
        {
            // Bounded: keep what was read before the deadline or post-exit grace.
        }

        return Encoding.UTF8.GetString(kept.GetBuffer(), 0, (int)kept.Length);
    }
}
