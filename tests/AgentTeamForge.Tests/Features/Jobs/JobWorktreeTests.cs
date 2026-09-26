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
    public async Task Subdirectory_cwd_maps_into_the_worktree_and_survives_an_agent_branch_switch()
    {
        using var f = new JobFixture();
        using var source = new TempStateDir();
        var sub = Directory.CreateDirectory(Path.Combine(source.Path, "odd dir -x")).FullName;
        File.WriteAllText(Path.Combine(sub, "tracked.txt"), "x");
        Git(source.Path, "init");
        Git(source.Path, "add", "-A");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "initial");
        var backend = new ScriptedBackend(r =>
        [
            new BackendEvidence.Session(r.Correlation, r.ResumeSessionId ?? "session-1"),
            new BackendEvidence.Result(r.Correlation, "done"),
        ]);
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, f.Admission, catalog.Names);

        var parent = accept.Execute(new SubmitJobRequest("one", "first", null, false) { Cwd = sub, Worktree = true }).Job!;
        await Dispatch(f, catalog);
        var mapped = Path.Combine(parent.WorktreePath!, "odd dir -x");
        Assert.Equal(mapped, backend.Started[0].WorkingDirectory?.TrimEnd('/'));

        // An agent that moves the worktree to its own branch must not break follow-ups.
        Git(parent.WorktreePath!, "checkout", "-b", "agent-branch");
        new FollowUpJob(f.Store, JobFixture.Operator, accept).Execute(new FollowUpRequest(parent.JobId, "second", "two"));
        await Dispatch(f, catalog);
        Assert.Equal(mapped, backend.Started[1].WorkingDirectory?.TrimEnd('/'));
        Assert.Equal("agent-branch", Git(parent.WorktreePath!, "rev-parse", "--abbrev-ref", "HEAD"));
    }

    [Fact]
    public void Concurrent_jobs_in_one_repository_get_separate_worktrees()
    {
        using var f = new JobFixture();
        using var source = new TempStateDir();
        Git(source.Path, "init");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        var jobs = Enumerable.Range(0, 6).Select(i => f.Accept().Execute(new SubmitJobRequest($"k{i}", "task", null, false)
        {
            Cwd = source.Path,
            Worktree = true,
        }).Job!).ToList();

        Parallel.ForEach(jobs, job => Assert.True(JobWorktree.Prepare(f.Store.GetJob(job.JobId)!)));
        Assert.All(jobs, job => Assert.Equal("atf/job-" + job.JobId, Git(job.WorktreePath!, "rev-parse", "--abbrev-ref", "HEAD")));
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
