using AgentTeamForge.DAL.Files;
using System.Diagnostics;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Git worktrees are created by the daemon from a pinned submit-time HEAD.</summary>
public static class JobWorktree
{
    static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    // Checkout of a large repository (and LFS/post-checkout hooks) can take minutes.
    static readonly TimeSpan AddTimeout = TimeSpan.FromMinutes(10);

    public static string? Head(string cwd)
    {
        var root = Git(cwd, QueryTimeout, "rev-parse", "--show-toplevel");
        return root is null ? null : Git(cwd, QueryTimeout, "rev-parse", "--verify", "HEAD");
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
            return Git(job.WorktreePath, QueryTimeout, "rev-parse", "--show-prefix") == string.Empty;
        }

        if (job.ParentJobId is not null || job.Cwd is null || job.WorktreeBranch is null || job.WorktreeBase is null)
        {
            return false;
        }

        try
        {
            var parent = Path.GetDirectoryName(job.WorktreePath)!;
            PrivateFiles.CreateDirectory(parent);

            return Git(job.Cwd, AddTimeout, "worktree", "add", "-b", job.WorktreeBranch, job.WorktreePath, job.WorktreeBase) is not null;
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
    static string? Git(string cwd, TimeSpan timeout, params string[] args)
    {
        try
        {
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

            // Drain stdout concurrently: a chatty hook must not block git on a full pipe.
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? output.GetAwaiter().GetResult().Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
