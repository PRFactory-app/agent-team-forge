using System.Diagnostics;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Git worktrees are created by the daemon from a pinned submit-time HEAD.</summary>
public static class JobWorktree
{
    public static string? Head(string cwd)
    {
        var root = Git(cwd, "rev-parse", "--show-toplevel");
        return root is null ? null : Git(cwd, "rev-parse", "--verify", "HEAD");
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
            return Git(job.WorktreePath, "rev-parse", "--abbrev-ref", "HEAD") == job.WorktreeBranch;
        }

        if (job.ParentJobId is not null || job.Cwd is null || job.WorktreeBranch is null || job.WorktreeBase is null)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(job.WorktreePath)!,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return Git(job.Cwd, "worktree", "add", "-b", job.WorktreeBranch, job.WorktreePath, job.WorktreeBase) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static string? Git(string cwd, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("-C");
            info.ArgumentList.Add(cwd);
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info);
            if (process is null || !process.WaitForExit(10_000) || process.ExitCode != 0)
            {
                if (process is { HasExited: false })
                {
                    process.Kill();
                }
                return null;
            }

            return process.StandardOutput.ReadToEnd().Trim() is { Length: > 0 } output ? output : "ok";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
