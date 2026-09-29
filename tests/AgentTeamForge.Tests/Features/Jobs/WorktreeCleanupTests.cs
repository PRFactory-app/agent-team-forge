using System.Diagnostics;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class WorktreeCleanupTests
{
    sealed class Env : IDisposable
    {
        readonly TempStateDir _dir = new();
        public JobFixture Fixture = new();
        public string Origin, Clone;
        public WorktreeCleanup Cleanup;

        public Env()
        {
            Origin = Path.Combine(_dir.Path, "origin.git");
            Clone = Path.Combine(_dir.Path, "clone");
            Git(_dir.Path, "init", "--bare", "-b", "main", Origin);
            Git(_dir.Path, "clone", Origin, Clone);
            Commit(Clone, "a.txt", "a");
            Git(Clone, "push", "origin", "HEAD:refs/heads/main");
            Git(Clone, "fetch", "origin");
            Git(Clone, "remote", "set-head", "origin", "main");
            Cleanup = new WorktreeCleanup(Fixture.Store, new BackendCatalog());
        }

        public string Path_(string id) => System.IO.Path.Combine(Cleanup.Root, id);

        public string Make(string id)
        {
            var path = Path_(id);
            Assert.True(JobWorktree.Prepare(Clone, path, "atf/job-" + id, Git(Clone, "rev-parse", "HEAD")));
            return path;
        }

        public void Dispose() { Fixture.Dispose(); _dir.Dispose(); }
    }

    [Fact]
    public async Task Merged_clean_worktree_is_removed_and_job_branch_deleted()
    {
        using var e = new Env();
        var path = e.Make("job_one");
        var sha = Commit(path, "b.txt", "b");
        Git(path, "push", "origin", "HEAD:refs/heads/main");
        Git(e.Clone, "fetch", "origin");

        var result = await e.Cleanup.RemoveAsync(path, false, false, true, TestContext.Current.CancellationToken);

        Assert.Equal("removed", result.Outcome);
        Assert.False(Directory.Exists(path));
        Assert.DoesNotContain("job_one", Git(e.Clone, "worktree", "list"));
        Assert.Equal("", Git(e.Clone, "branch", "--list", "atf/job-job_one"));
        Assert.Equal(sha, Git(e.Origin, "rev-parse", "main"));
    }

    [Fact]
    public async Task Unpushed_commit_is_kept_even_with_force()
    {
        using var e = new Env();
        var path = e.Make("job_two");
        Commit(path, "b.txt", "local only");

        var result = await e.Cleanup.RemoveAsync(path, true, false, false, TestContext.Current.CancellationToken);

        Assert.Equal(("kept", "unmerged_commits"), (result.Outcome, result.Reason));
        Assert.True(Directory.Exists(path));
        Assert.NotEqual("", Git(e.Clone, "branch", "--list", "atf/job-job_two"));
    }

    [Fact]
    public async Task Dirty_or_unknown_ignored_files_block_until_force()
    {
        using var e = new Env();
        var path = e.Make("job_three");
        File.WriteAllText(Path.Combine(path, "x.cs"), "x");
        Assert.Equal("dirty", (await e.Cleanup.RemoveAsync(path, false, false, false, TestContext.Current.CancellationToken)).Reason);
        File.Delete(Path.Combine(path, "x.cs"));

        Commit(path, ".gitignore", ".env\nbin/\nobj/\n");
        Git(path, "push", "origin", "HEAD:refs/heads/main");
        Git(e.Clone, "fetch", "origin");
        File.WriteAllText(Path.Combine(path, ".env"), "SECRET");
        var blocked = await e.Cleanup.RemoveAsync(path, false, false, false, TestContext.Current.CancellationToken);
        Assert.Equal("ignored_files", blocked.Reason);
        Assert.Contains(".env", blocked.Details!);

        File.Delete(Path.Combine(path, ".env"));
        Directory.CreateDirectory(Path.Combine(path, "bin"));
        File.WriteAllText(Path.Combine(path, "bin", "o.dll"), "x");
        Directory.CreateDirectory(Path.Combine(path, "obj"));
        File.WriteAllText(Path.Combine(path, "obj", "o.dll"), "x");
        Assert.Equal("removed", (await e.Cleanup.RemoveAsync(path, false, false, false, TestContext.Current.CancellationToken)).Outcome);

        var forced = e.Make("job_four");
        Commit(forced, ".gitignore", ".env\n");
        Git(forced, "push", "origin", "HEAD:refs/heads/feat4");
        Git(e.Clone, "fetch", "origin");
        File.WriteAllText(Path.Combine(forced, ".env"), "SECRET");
        Assert.Equal("removed", (await e.Cleanup.RemoveAsync(forced, true, false, false, TestContext.Current.CancellationToken)).Outcome);
        Assert.False(Directory.Exists(forced));
    }

    [Fact]
    public async Task Active_or_live_job_worktree_is_kept()
    {
        using var e = new Env();
        var backend = new ScriptedBackend(r => [new BackendEvidence.Session(r.Correlation, "s1"), new BackendEvidence.Result(r.Correlation, "done")]);
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var accept = new AcceptJob(e.Fixture.Store, JobFixture.Operator, e.Fixture.Limits, true, e.Fixture.Admission, catalog.Names);
        var parent = accept.Execute(new SubmitJobRequest("k1", "first", null, false) { Cwd = e.Clone, Worktree = true }).Job!;

        Assert.True(JobWorktree.Prepare(e.Fixture.Store.GetJob(parent.JobId)!));
        var queued = await e.Cleanup.RemoveAsync(parent.WorktreePath!, false, false, false, TestContext.Current.CancellationToken);
        Assert.Equal("job_active", queued.Reason);

        using (var dispatcher = new DispatchJob(e.Fixture.Store, catalog, e.Fixture.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { }))
        {
            await dispatcher.RunAttemptAsync(e.Fixture.Store.BeginNextAttempt()!, TestContext.Current.CancellationToken);
        }
        Assert.Equal(JobStatus.Completed, e.Fixture.Store.GetJob(parent.JobId)!.Status);
        new FollowUpJob(e.Fixture.Store, JobFixture.Operator, accept).Execute(new FollowUpRequest(parent.JobId, "second", "k2"));

        var chained = await e.Cleanup.RemoveAsync(parent.WorktreePath!, false, false, false, TestContext.Current.CancellationToken);
        Assert.Equal("job_active", chained.Reason);
        Assert.True(Directory.Exists(parent.WorktreePath));
    }

    [Fact]
    public async Task Auto_mode_keeps_pushed_but_unmerged_branch()
    {
        using var e = new Env();
        var path = e.Make("job_five");
        Commit(path, "b.txt", "feature");
        Git(path, "push", "origin", "HEAD:refs/heads/feature");
        Git(e.Clone, "fetch", "origin");

        var auto = await e.Cleanup.RemoveAsync(path, false, false, true, TestContext.Current.CancellationToken);
        Assert.Equal(("kept", "not_merged"), (auto.Outcome, auto.Reason));

        Assert.Equal("removed", (await e.Cleanup.RemoveAsync(path, false, false, false, TestContext.Current.CancellationToken)).Outcome);
    }

    static string Commit(string cwd, string file, string content)
    {
        File.WriteAllText(Path.Combine(cwd, file), content);
        Git(cwd, "add", file);
        Git(cwd, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "c " + file);
        return Git(cwd, "rev-parse", "HEAD");
    }

    static string Git(string cwd, params string[] args)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(cwd);
        foreach (var arg in args) { info.ArgumentList.Add(arg); }
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
