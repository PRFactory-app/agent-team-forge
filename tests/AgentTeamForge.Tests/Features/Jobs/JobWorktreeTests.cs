using System.Diagnostics;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class JobWorktreeTests
{
    [Fact]
    public async Task Submit_creates_a_worktree_and_follow_up_reuses_it()
    {
        using var f = new JobFixture();
        using var source = new TempStateDir();
        Git(source.Path, "init");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        var head = Git(source.Path, "rev-parse", "HEAD");
        var backend = new ScriptedBackend(r =>
        [
            new BackendEvidence.Session(r.Correlation, r.ResumeSessionId ?? "session-1"),
            new BackendEvidence.Result(r.Correlation, "done"),
        ]);
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, f.Admission, catalog.Names);

        var parent = accept.Execute(new SubmitJobRequest("one", "first", null, false)
        {
            Cwd = source.Path,
            Worktree = true,
        }).Job!;
        Assert.Equal(Path.Combine(Path.GetDirectoryName(f.DatabasePath)!, "worktrees", parent.JobId), parent.WorktreePath);
        Assert.Equal("atf/job-" + parent.JobId, parent.WorktreeBranch);
        Assert.Equal(head, f.Store.GetJob(parent.JobId)!.WorktreeBase);
        await Dispatch(f, catalog);
        Assert.Equal(JobStatus.Completed, f.Store.GetJob(parent.JobId)!.Status);
        Assert.Equal(parent.WorktreePath, backend.Started[0].WorkingDirectory);
        Assert.Equal(head, Git(parent.WorktreePath!, "rev-parse", "HEAD"));

        File.WriteAllText(Path.Combine(parent.WorktreePath!, "keep.txt"), "uncommitted");
        var child = new FollowUpJob(f.Store, JobFixture.Operator, accept)
            .Execute(new FollowUpRequest(parent.JobId, "second", "two")).Job!;
        Assert.Equal(parent.WorktreePath, child.WorktreePath);
        Assert.Equal(parent.WorktreeBranch, child.WorktreeBranch);
        await Dispatch(f, catalog);
        Assert.Equal(parent.WorktreePath, backend.Started[1].WorkingDirectory);
        Assert.Equal("uncommitted", File.ReadAllText(Path.Combine(parent.WorktreePath!, "keep.txt")));
        Assert.Equal(parent.WorktreePath, f.Get().Execute(child.JobId).Job!.WorktreePath);
        Assert.Contains(f.List().Execute(new ListJobsRequest()).Page!.Jobs,
            j => j.JobId == child.JobId && j.WorktreePath == parent.WorktreePath && j.WorktreeBranch == parent.WorktreeBranch);
    }

    [Fact]
    public void Worktree_requires_a_git_repository()
    {
        using var f = new JobFixture();
        using var cwd = new TempStateDir();
        var result = f.Accept().Execute(new SubmitJobRequest("one", "task", null, false)
        {
            Cwd = cwd.Path,
            Worktree = true,
        });
        Assert.Equal(JobErrors.CwdNotGitRepo, result.Error);
        Assert.Equal(0, f.Store.CountUnattemptedIntents());
    }

    static async Task Dispatch(JobFixture f, BackendCatalog catalog)
    {
        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        await dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
    }

    static string Git(string cwd, params string[] args)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(cwd);
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info)!;
        Assert.True(process.WaitForExit(10_000));
        var output = process.StandardOutput.ReadToEnd().Trim();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        return output;
    }
}
