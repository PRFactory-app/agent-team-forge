using System.Diagnostics;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
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

        /// <param name="linkedState">Reach the state directory through a symlink, as macOS /tmp and a linked XDG_STATE_HOME do.</param>
        public Env(bool linkedState = false)
        {
            Origin = Path.Combine(_dir.Path, "origin.git");
            Clone = Path.Combine(_dir.Path, "clone");
            Git(_dir.Path, "init", "--bare", "-b", "main", Origin);
            Git(_dir.Path, "clone", Origin, Clone);
            Commit(Clone, "a.txt", "a");
            Git(Clone, "push", "origin", "HEAD:refs/heads/main");
            Git(Clone, "fetch", "origin");
            Git(Clone, "remote", "set-head", "origin", "main");
            var store = Fixture.Store;
            if (linkedState)
            {
                var real = Directory.CreateDirectory(Path.Combine(_dir.Path, "real-state")).FullName;
                var linked = Path.Combine(_dir.Path, "linked-state");
                Directory.CreateSymbolicLink(linked, real);
                store = new JobStore(JobDatabase.Create(Path.Combine(linked, "jobs.db"), Fixture.Limits.BusyTimeout), DurabilityCheckpoints.None);
            }
            Cleanup = new WorktreeCleanup(store, new BackendCatalog());
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
    public async Task Worktree_under_a_symlinked_state_directory_is_owned_and_removed()
    {
        using var e = new Env(linkedState: true);
        Assert.NotNull(new DirectoryInfo(Path.GetDirectoryName(e.Cleanup.Root)!).LinkTarget);
        var path = e.Make("job_linked");
        Commit(path, "b.txt", "b");
        Git(path, "push", "origin", "HEAD:refs/heads/main");
        Git(e.Clone, "fetch", "origin");

        Assert.Equal("would_remove", (await e.Cleanup.RemoveAsync(path, false, true, false, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal("removed", (await e.Cleanup.RemoveAsync(path, false, false, true, TestContext.Current.CancellationToken)).Outcome);
        Assert.False(Directory.Exists(path));
        Assert.Equal("", Git(e.Clone, "branch", "--list", "atf/job-job_linked"));
    }

    [Fact]
    public async Task Symlinked_entry_under_a_symlinked_state_directory_is_still_rejected()
    {
        using var e = new Env(linkedState: true);
        var path = e.Make("job_real");
        var link = e.Path_("job_alias");
        Directory.CreateSymbolicLink(link, path);

        var result = await e.Cleanup.RemoveAsync(link, true, false, false, TestContext.Current.CancellationToken);

        Assert.Equal(("kept", "not_owned_path"), (result.Outcome, result.Reason));
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task Job_lookup_distinguishes_unknown_job_and_job_without_worktree()
    {
        using var e = new Env();
        var plain = e.Fixture.Submit("plain");
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal((null, JobErrors.NotFound), await e.Cleanup.RemoveJobAsync("job_missing", false, true, ct));
        Assert.Equal((null, JobErrors.NoWorktree), await e.Cleanup.RemoveJobAsync(plain.JobId, false, true, ct));
        var remove = new RemoveWorktree(e.Fixture.Store, JobFixture.Operator, e.Cleanup);
        Assert.Equal((null, JobErrors.NoWorktree), await remove.ExecuteAsync(plain.JobId, false, true, ct));
        Assert.Equal((null, JobErrors.NotFound), await remove.ExecuteAsync("job_missing", false, true, ct));

        var job = e.Fixture.Accept().Execute(new SubmitJobRequest("tree", "work", null, false) { Cwd = e.Clone, Worktree = true }).Job!;
        Assert.True(JobWorktree.Prepare(e.Fixture.Store.GetJob(job.JobId)!, TestContext.Current.CancellationToken));
        var (found, error) = await e.Cleanup.RemoveJobAsync(job.JobId, false, true, ct);
        Assert.Null(error);
        Assert.Equal(("kept", "job_active"), (found!.Outcome, found.Reason));
    }

    [Fact]
    public async Task Git_refusal_reports_gits_reason()
    {
        using var e = new Env();
        var path = e.Make("job_locked");
        Git(e.Clone, "worktree", "lock", "--reason", "held by test", path);

        var result = await e.Cleanup.RemoveAsync(path, false, false, false, TestContext.Current.CancellationToken);

        Assert.Equal(("kept", "git_refused"), (result.Outcome, result.Reason));
        Assert.Contains(result.Details!, line => line.Contains("locked", StringComparison.Ordinal));
        Assert.True(Directory.Exists(path));
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
    public async Task Tools_symlink_does_not_block_removal_but_tools_directory_does()
    {
        using var e = new Env();
        var sdk = Path.Combine(Path.GetDirectoryName(e.Clone)!, "sdk");
        Directory.CreateDirectory(sdk);
        File.WriteAllText(Path.Combine(sdk, "dotnet"), "x");

        var a = e.Make("job_tools_link");
        Commit(a, ".gitignore", ".tools\n");
        Git(a, "push", "origin", "HEAD:refs/heads/feat-link");
        Git(e.Clone, "fetch", "origin");
        Directory.CreateSymbolicLink(Path.Combine(a, ".tools"), sdk);
        var linked = await e.Cleanup.RemoveAsync(a, false, false, false, TestContext.Current.CancellationToken);
        Assert.Equal("removed", linked.Outcome);
        Assert.False(Directory.Exists(a));
        Assert.True(File.Exists(Path.Combine(sdk, "dotnet")));

        var b = e.Make("job_tools_dir");
        Commit(b, ".gitignore", ".tools\n");
        Git(b, "push", "origin", "HEAD:refs/heads/feat-dir");
        Git(e.Clone, "fetch", "origin");
        Directory.CreateDirectory(Path.Combine(b, ".tools", "dotnet11"));
        File.WriteAllText(Path.Combine(b, ".tools", "dotnet11", "dotnet"), "x");
        var blocked = await e.Cleanup.RemoveAsync(b, false, false, false, TestContext.Current.CancellationToken);
        Assert.Equal("ignored_files", blocked.Reason);
        Assert.Contains(".tools/", blocked.Details!);
    }

    [Fact]
    public async Task Active_or_live_job_worktree_is_kept()
    {
        using var e = new Env();
        var backend = new ScriptedBackend(r => [new BackendEvidence.Session(r.Correlation, "s1"), new BackendEvidence.Result(r.Correlation, "done")]);
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var accept = new AcceptJob(e.Fixture.Store, JobFixture.Operator, e.Fixture.Limits, true, e.Fixture.Admission, catalog.Names);
        var parent = accept.Execute(new SubmitJobRequest("k1", "first", null, false) { Cwd = e.Clone, Worktree = true }).Job!;

        Assert.True(JobWorktree.Prepare(e.Fixture.Store.GetJob(parent.JobId)!, TestContext.Current.CancellationToken));
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

    [Fact]
    public async Task Traversal_and_symlink_paths_are_rejected()
    {
        using var e = new Env();
        var path = e.Make("job_path");
        var link = e.Path_("job_link");
        Directory.CreateSymbolicLink(link, path);
        var outside = Path.Combine(Path.GetDirectoryName(e.Origin)!, "job_outside");
        Assert.True(JobWorktree.Prepare(e.Clone, outside, "atf/job-job_outside", Git(e.Clone, "rev-parse", "HEAD"), TestContext.Current.CancellationToken));

        Assert.Equal("not_owned_path", (await e.Cleanup.RemoveAsync(
            outside, true, false, false, TestContext.Current.CancellationToken)).Reason);
        Assert.Equal("not_owned_path", (await e.Cleanup.RemoveAsync(
            link, true, false, false, TestContext.Current.CancellationToken)).Reason);
        Assert.True(Directory.Exists(path));
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public async Task Switched_branch_does_not_hide_unpushed_job_branch_tip()
    {
        using var e = new Env();
        var path = e.Make("job_switched");
        Commit(path, "local.txt", "unpublished");
        var baseSha = Git(e.Clone, "rev-parse", "HEAD");
        Git(path, "switch", "-c", "feature", baseSha);

        var result = await e.Cleanup.RemoveAsync(path, true, false, false, TestContext.Current.CancellationToken);

        Assert.Equal("unmerged_commits", result.Reason);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task Fenced_or_live_terminal_job_keeps_its_worktree()
    {
        using var e = new Env();
        var backend = new IdleBackend();
        e.Cleanup = new WorktreeCleanup(e.Fixture.Store,
            new BackendCatalog().Register(BackendCatalog.Fake, () => backend));
        var job = e.Fixture.Accept().Execute(new SubmitJobRequest("live", "work", null, false)
        { Cwd = e.Clone, Worktree = true }).Job!;
        Assert.True(JobWorktree.Prepare(e.Fixture.Store.GetJob(job.JobId)!, TestContext.Current.CancellationToken));
        var claim = e.Fixture.Store.BeginNextAttempt()!;
        var run = new RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation);
        e.Fixture.Store.RecordSession(run, "native-1");
        e.Fixture.Store.Complete(run, "done");

        e.Fixture.Store.FenceSession(job.JobId);
        Assert.Equal("job_active", (await e.Cleanup.RemoveAsync(job.WorktreePath!, false, false, false,
            TestContext.Current.CancellationToken)).Reason);
        e.Fixture.Store.ReconcileStoppedJob(job.JobId);
        Assert.Equal("agent_live", (await e.Cleanup.RemoveAsync(job.WorktreePath!, false, false, false,
            TestContext.Current.CancellationToken)).Reason);
        Assert.True(Directory.Exists(job.WorktreePath));
    }

    [Fact]
    public async Task Reconciled_job_keeps_worktree_until_cancelled_then_removes_normally()
    {
        using var e = new Env();
        var job = e.Fixture.Accept().Execute(new SubmitJobRequest("recon", "work", null, false)
        { Cwd = e.Clone, Worktree = true }).Job!;
        Assert.True(JobWorktree.Prepare(e.Fixture.Store.GetJob(job.JobId)!, TestContext.Current.CancellationToken));
        var claim = e.Fixture.Store.BeginNextAttempt()!;
        Assert.True(e.Fixture.Store.EndUnsuccessfully(new RunRef(job.JobId, claim.RunId, claim.Generation, claim.Correlation),
            JobStatus.NeedsReconciliation, "interactive_agent_exited"));

        Assert.Equal("job_active", (await e.Cleanup.RemoveAsync(job.WorktreePath!, false, false, false,
            TestContext.Current.CancellationToken)).Reason);
        e.Fixture.Store.CancelReconciled(job.JobId, JobFixture.Operator.Principal, JobFixture.Operator.Team);

        Assert.Equal("removed", (await e.Cleanup.RemoveAsync(job.WorktreePath!, false, false, false,
            TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task Moved_job_branch_is_not_deleted_after_worktree_removal()
    {
        using var e = new Env();
        var path = e.Make("job_cas");
        var moved = Commit(e.Clone, "later.txt", "new tip");
        e.Cleanup.BeforeBranchDelete = (repo, branch, _) => Git(repo, "update-ref", branch, moved);

        var result = await e.Cleanup.RemoveAsync(path, false, false, false, TestContext.Current.CancellationToken);

        Assert.Equal("removed", result.Outcome);
        Assert.Equal(moved, Git(e.Clone, "rev-parse", "refs/heads/atf/job-job_cas"));
    }

    [Fact]
    public async Task Cleanup_concurrent_with_add_in_one_repository_leaves_both_consistent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var e = new Env();
        var sha = Git(e.Clone, "rev-parse", "HEAD");
        var old = Enumerable.Range(0, 8).Select(i => e.Make($"job_old{i}")).ToArray();

        var removals = old.Select(path => Task.Run(() => e.Cleanup.RemoveAsync(path, false, false, false, ct), ct)).ToArray();
        var adds = Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => JobWorktree.Prepare(e.Clone, e.Path_($"job_new{i}"), $"atf/job-job_new{i}", sha, ct), ct)).ToArray();

        Assert.All(await Task.WhenAll(removals), r => Assert.Equal("removed", r.Outcome));
        Assert.All(await Task.WhenAll(adds), Assert.True);
        Assert.All(old, path => Assert.False(Directory.Exists(path)));
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal($"atf/job-job_new{i}", Git(e.Path_($"job_new{i}"), "rev-parse", "--abbrev-ref", "HEAD"));
        }
        Assert.Equal(9, Git(e.Clone, "worktree", "list", "--porcelain").Split('\n').Count(l => l.StartsWith("worktree ", StringComparison.Ordinal)));
        Assert.Empty(Git(e.Clone, "worktree", "prune", "--dry-run", "--verbose"));
    }

    sealed class IdleBackend : IJobBackend, IInteractiveSessionStop
    {
        public IBackendRun Start(BackendRequest request) => throw new InvalidOperationException("not dispatched");
        public bool HasIdleSession(string sessionId) => sessionId == "native-1";
        public bool StopIdleSession(string sessionId) => throw new InvalidOperationException("not stopped");
        public void StopAllIdleSessions() { }
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
