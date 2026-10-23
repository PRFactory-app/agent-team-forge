using System.Diagnostics;
using System.Text;
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
        Assert.Equal(mapped, f.Get().Execute(parent.JobId).Job!.Cwd);

        // An agent that moves the worktree to its own branch must not break follow-ups.
        Git(parent.WorktreePath!, "checkout", "-b", "agent-branch");
        new FollowUpJob(f.Store, JobFixture.Operator, accept).Execute(new FollowUpRequest(parent.JobId, "second", "two"));
        await Dispatch(f, catalog);
        Assert.Equal(mapped, backend.Started[1].WorkingDirectory?.TrimEnd('/'));
        Assert.Equal("agent-branch", Git(parent.WorktreePath!, "rev-parse", "--abbrev-ref", "HEAD"));
    }

    [Fact]
    public async Task Follow_up_runs_after_the_agent_moves_its_worktree_to_a_branch_without_the_submit_base()
    {
        using var f = new JobFixture();
        using var source = new TempStateDir();
        Git(source.Path, "init", "-b", "main");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "main");
        // The lead submits from a feature branch, so the pinned base is not on main.
        Git(source.Path, "checkout", "-b", "feature");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "feature");
        var backend = new ScriptedBackend(r =>
        [
            new BackendEvidence.Session(r.Correlation, r.ResumeSessionId ?? "session-1"),
            new BackendEvidence.Result(r.Correlation, "done"),
        ]);
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, f.Admission, catalog.Names);
        var parent = accept.Execute(new SubmitJobRequest("one", "first", null, false) { Cwd = source.Path, Worktree = true }).Job!;
        await Dispatch(f, catalog);

        // As told by its lead, the agent starts its own branch from main and commits there.
        Git(parent.WorktreePath!, "checkout", "-b", "planning/x", "main");
        Git(parent.WorktreePath!, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "plan");
        var child = new FollowUpJob(f.Store, JobFixture.Operator, accept).Execute(new FollowUpRequest(parent.JobId, "second", "two")).Job!;
        await Dispatch(f, catalog);

        Assert.Equal(JobStatus.Completed, f.Store.GetJob(child.JobId)!.Status);
        Assert.Equal(parent.WorktreePath, backend.Started[1].WorkingDirectory);
        Assert.Equal("planning/x", Git(parent.WorktreePath!, "rev-parse", "--abbrev-ref", "HEAD"));
    }

    [Fact]
    public async Task Existing_job_path_that_is_not_a_worktree_of_the_job_repository_is_refused_with_the_failed_check()
    {
        using var f = new JobFixture();
        using var source = new TempStateDir();
        Git(source.Path, "init");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        var (catalog, accept) = Fake(f);
        var parent = accept.Execute(new SubmitJobRequest("one", "first", null, false) { Cwd = source.Path, Worktree = true }).Job!;
        await Dispatch(f, catalog);

        // Another repository at the job's path does not hold the job's base.
        Directory.Delete(parent.WorktreePath!, recursive: true);
        Directory.CreateDirectory(parent.WorktreePath!);
        Git(parent.WorktreePath!, "init");
        Git(parent.WorktreePath!, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "other");
        var refusal = JobWorktree.Prepare(f.Store.GetJob(parent.JobId)!, TestContext.Current.CancellationToken);
        Assert.Equal(JobWorktree.Unavailable, refusal?.Reason);

        var child = new FollowUpJob(f.Store, JobFixture.Operator, accept).Execute(new FollowUpRequest(parent.JobId, "second", "two")).Job!;
        var logs = new List<string>();
        await Dispatch(f, catalog, logs.Add);
        var failed = f.Store.GetJob(child.JobId)!;
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Equal(JobWorktree.Unavailable, failed.ReasonCode);
        Assert.Contains(refusal!.Detail, failed.ResultText);
        Assert.Contains("submit a new job", failed.ResultText);
        Assert.Contains(logs, l => l.Contains(child.JobId, StringComparison.Ordinal) && l.Contains(refusal.Detail, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Follow_up_whose_worktree_was_removed_fails_as_missing_with_a_recovery_hint()
    {
        using var f = new JobFixture();
        using var source = new TempStateDir();
        Git(source.Path, "init");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        var (catalog, accept) = Fake(f);
        var parent = accept.Execute(new SubmitJobRequest("one", "first", null, false) { Cwd = source.Path, Worktree = true }).Job!;
        await Dispatch(f, catalog);

        Git(source.Path, "worktree", "remove", "--force", parent.WorktreePath!);
        var child = new FollowUpJob(f.Store, JobFixture.Operator, accept).Execute(new FollowUpRequest(parent.JobId, "second", "two")).Job!;
        await Dispatch(f, catalog);

        var failed = f.Store.GetJob(child.JobId)!;
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Equal(JobWorktree.Missing, failed.ReasonCode);
        Assert.Contains("submit a new job", failed.ResultText);
        Assert.False(Directory.Exists(parent.WorktreePath));
    }

    [Fact]
    public async Task A_workspace_check_that_times_out_requeues_the_follow_up_instead_of_failing_it()
    {
        using var f = new JobFixture();
        using var source = new TempStateDir();
        Git(source.Path, "init");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        var (catalog, accept) = Fake(f);
        var parent = accept.Execute(new SubmitJobRequest("one", "first", null, false) { Cwd = source.Path, Worktree = true }).Job!;
        await Dispatch(f, catalog);
        var child = new FollowUpJob(f.Store, JobFixture.Operator, accept).Execute(new FollowUpRequest(parent.JobId, "second", "two")).Job!;

        // A loaded machine: git answers only after the check's deadline.
        JobWorktree.CheckTimeout.Value = TimeSpan.FromSeconds(1);
        JobWorktree.ReaderDelay.Value = TimeSpan.FromSeconds(3);
        try { await Dispatch(f, catalog); }
        finally
        {
            JobWorktree.CheckTimeout.Value = null;
            JobWorktree.ReaderDelay.Value = TimeSpan.Zero;
        }
        Assert.Equal(JobStatus.Queued, f.Store.GetJob(child.JobId)!.Status);

        await Dispatch(f, catalog);
        Assert.Equal(JobStatus.Completed, f.Store.GetJob(child.JobId)!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_separate_clone_that_holds_the_base_on_a_divergent_branch_is_refused(bool submittedCwdDeleted)
    {
        using var f = new JobFixture();
        using var source = new TempStateDir();
        Git(source.Path, "init");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        var (catalog, accept) = Fake(f);
        var parent = accept.Execute(new SubmitJobRequest("one", "first", null, false) { Cwd = source.Path, Worktree = true }).Job!;
        await Dispatch(f, catalog);

        // A clone holds the base commit but has its own repository; its HEAD does not contain the base.
        Git(source.Path, "worktree", "remove", "--force", parent.WorktreePath!);
        Git(source.Path, "clone", "--quiet", source.Path, parent.WorktreePath!);
        Git(parent.WorktreePath!, "checkout", "--orphan", "elsewhere");
        Git(parent.WorktreePath!, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "unrelated");
        if (submittedCwdDeleted) { Directory.Delete(source.Path, recursive: true); }
        Assert.Equal(JobWorktree.Unavailable, JobWorktree.Prepare(f.Store.GetJob(parent.JobId)!, TestContext.Current.CancellationToken)?.Reason);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_linked_worktree_of_another_clone_at_the_job_path_is_refused(bool submittedCwdDeleted, bool legacyRow)
    {
        using var f = new JobFixture();
        using var source = new TempStateDir();
        using var other = new TempStateDir();
        Git(source.Path, "init");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        var (catalog, accept) = Fake(f);
        var parent = accept.Execute(new SubmitJobRequest("one", "first", null, false) { Cwd = source.Path, Worktree = true }).Job!;
        Assert.Equal(Git(source.Path, "rev-parse", "--path-format=absolute", "--git-common-dir"), f.Store.GetJob(parent.JobId)!.WorktreeCommonDir);
        await Dispatch(f, catalog);
        // A row accepted before v36 has no recorded repository and falls back to the submitted cwd.
        if (legacyRow) { ForgetCommonDir(f, parent.JobId); }

        // The other clone holds the base and links its own worktree at the job path.
        Git(source.Path, "worktree", "remove", "--force", parent.WorktreePath!);
        var clone = Path.Combine(other.Path, "clone");
        Git(other.Path, "clone", "--quiet", source.Path, clone);
        Git(clone, "worktree", "add", "--quiet", "-b", "foreign", parent.WorktreePath!);
        if (submittedCwdDeleted) { Directory.Delete(source.Path, recursive: true); }
        var refusal = JobWorktree.Prepare(f.Store.GetJob(parent.JobId)!, TestContext.Current.CancellationToken);
        Assert.Equal(JobWorktree.Unavailable, refusal?.Reason);
        Assert.Contains("not of the job's repository", refusal!.Detail);
    }

    [Fact]
    public async Task A_legacy_row_whose_cwd_repository_query_times_out_is_retried_not_accepted()
    {
        // The slow git is a shell wrapper; Windows has no Unix execute bit for it.
        if (OperatingSystem.IsWindows()) { return; }
        using var f = new JobFixture();
        using var source = new TempStateDir();
        using var tools = new TempStateDir();
        Git(source.Path, "init");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        var (catalog, accept) = Fake(f);
        var parent = accept.Execute(new SubmitJobRequest("one", "first", null, false) { Cwd = source.Path, Worktree = true }).Job!;
        await Dispatch(f, catalog);
        ForgetCommonDir(f, parent.JobId);

        // Only git in the submitted cwd answers after the deadline.
        var slowGit = Path.Combine(tools.Path, "git");
        File.WriteAllText(slowGit, $"#!/bin/sh\nif [ \"$2\" = '{source.Path}' ]; then sleep 3; fi\nexec git \"$@\"\n");
        File.SetUnixFileMode(slowGit, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        JobWorktree.GitProgram.Value = slowGit;
        JobWorktree.CheckTimeout.Value = TimeSpan.FromSeconds(1);
        try
        {
            Assert.Throws<WorkspaceCheckIncompleteException>(() => JobWorktree.Prepare(f.Store.GetJob(parent.JobId)!, TestContext.Current.CancellationToken));
        }
        finally
        {
            JobWorktree.GitProgram.Value = null;
            JobWorktree.CheckTimeout.Value = null;
        }
    }

    static void ForgetCommonDir(JobFixture f, string jobId)
    {
        using var connection = f.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET worktree_common_dir=NULL WHERE job_id=$id";
        command.Parameters.AddWithValue("$id", jobId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    [Fact]
    public async Task A_plain_folder_at_the_job_path_is_refused()
    {
        using var f = new JobFixture();
        using var source = new TempStateDir();
        Git(source.Path, "init");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        var (catalog, accept) = Fake(f);
        var parent = accept.Execute(new SubmitJobRequest("one", "first", null, false) { Cwd = source.Path, Worktree = true }).Job!;
        await Dispatch(f, catalog);

        Git(source.Path, "worktree", "remove", "--force", parent.WorktreePath!);
        Directory.CreateDirectory(parent.WorktreePath!);
        Assert.Equal(JobWorktree.Unavailable, JobWorktree.Prepare(f.Store.GetJob(parent.JobId)!, TestContext.Current.CancellationToken)?.Reason);
    }

    [Fact]
    public async Task Workspace_checks_that_keep_timing_out_fail_after_three_durable_tries()
    {
        using var f = new JobFixture();
        using var source = new TempStateDir();
        Git(source.Path, "init");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        var backend = new ScriptedBackend(r =>
        [
            new BackendEvidence.Session(r.Correlation, r.ResumeSessionId ?? "session-1"),
            new BackendEvidence.Result(r.Correlation, "done"),
        ]);
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, f.Admission, catalog.Names);
        var parent = accept.Execute(new SubmitJobRequest("one", "first", null, false) { Cwd = source.Path, Worktree = true }).Job!;
        await Dispatch(f, catalog);
        var child = new FollowUpJob(f.Store, JobFixture.Operator, accept).Execute(new FollowUpRequest(parent.JobId, "second", "two")).Job!;

        JobWorktree.CheckTimeout.Value = TimeSpan.FromSeconds(1);
        JobWorktree.ReaderDelay.Value = TimeSpan.FromSeconds(2);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                // A restarted daemon reads the tries from the database, not from memory.
                var store = f.NewStore();
                using var dispatcher = new DispatchJob(store, catalog, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
                var claim = store.BeginNextAttempt();
                Assert.Equal(child.JobId, claim?.Job.JobId);
                await dispatcher.RunAttemptAsync(claim!, CancellationToken.None);
            }
        }
        finally
        {
            JobWorktree.CheckTimeout.Value = null;
            JobWorktree.ReaderDelay.Value = TimeSpan.Zero;
        }
        var failed = f.Store.GetJob(child.JobId)!;
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Equal(JobWorktree.Unavailable, failed.ReasonCode);
        Assert.Equal(3, f.Store.GetRuns(child.JobId).Count);
        Assert.Null(f.Store.BeginNextAttempt());
        Assert.Single(backend.Started);
    }

    [Theory]
    [InlineData("stopped")]
    [InlineData("timeout")]
    public async Task A_stop_or_timeout_during_an_unfinished_workspace_check_releases_the_session_fence(string reason)
    {
        using var f = new JobFixture();
        using var source = new TempStateDir();
        Git(source.Path, "init");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        var (catalog, accept) = Fake(f);
        var parent = accept.Execute(new SubmitJobRequest("one", "first", null, false) { Cwd = source.Path, Worktree = true }).Job!;
        await Dispatch(f, catalog);
        var follow = new FollowUpJob(f.Store, JobFixture.Operator, accept);
        var child = follow.Execute(new FollowUpRequest(parent.JobId, "second", "two") { TimeoutSeconds = reason == "timeout" ? 1 : null }).Job!;

        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        var readers = new List<Task>();
        JobWorktree.CheckTimeout.Value = TimeSpan.FromSeconds(3);
        JobWorktree.ReaderDelay.Value = TimeSpan.FromSeconds(5);
        JobWorktree.ReaderCompletions.Value = readers;
        var claim = f.Store.BeginNextAttempt()!;
        Task attempt;
        // The check runs synchronously; its own thread keeps the seams it started with.
        try { attempt = Task.Run(() => dispatcher.RunAttemptAsync(claim, CancellationToken.None), TestContext.Current.CancellationToken); }
        finally
        {
            JobWorktree.CheckTimeout.Value = null;
            JobWorktree.ReaderDelay.Value = TimeSpan.Zero;
            JobWorktree.ReaderCompletions.Value = null;
        }
        if (reason == "stopped")
        {
            await Bounded.Until(() => readers.Count > 0, "the workspace check to start");
            f.Store.CancelOwned(child.JobId, "stopped");
            dispatcher.CancelRunning(child.JobId);
        }
        await attempt.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);

        var cancelled = f.Store.GetJob(child.JobId)!;
        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        Assert.Equal(reason, cancelled.ReasonCode);
        Assert.False(f.Store.IsSessionFenced(child.JobId));
        var next = follow.Execute(new FollowUpRequest(parent.JobId, "third", "three")).Job!;
        await Dispatch(f, catalog);
        Assert.Equal(JobStatus.Completed, f.Store.GetJob(next.JobId)!.Status);
    }

    [Fact]
    public async Task Job_views_survive_a_deleted_submitted_cwd()
    {
        using var f = new JobFixture();
        using var source = new TempStateDir();
        var sub = Directory.CreateDirectory(Path.Combine(source.Path, "sub")).FullName;
        File.WriteAllText(Path.Combine(sub, "tracked.txt"), "x");
        Git(source.Path, "init");
        Git(source.Path, "add", "-A");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "initial");
        var backend = new ScriptedBackend(r =>
        [
            new BackendEvidence.Session(r.Correlation, "session-1"),
            new BackendEvidence.Result(r.Correlation, "done"),
        ]);
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var job = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, f.Admission, catalog.Names)
            .Execute(new SubmitJobRequest("one", "first", null, false) { Cwd = sub, Worktree = true }).Job!;
        await Dispatch(f, catalog);

        // Only the subdirectory gone: its counterpart in the worktree is still the working directory.
        Directory.Delete(sub, recursive: true);
        Assert.Equal(Path.Combine(job.WorktreePath!, "sub"), f.Get().Execute(job.JobId).Job!.Cwd);

        // The whole checkout gone: the views fall back to the worktree itself.
        if (OperatingSystem.IsWindows()) { source.Dispose(); }
        else { Directory.Delete(source.Path, recursive: true); }
        Assert.Equal(job.WorktreePath, f.Get().Execute(job.JobId).Job!.Cwd);
        Assert.Contains(f.List().Execute(new ListJobsRequest()).Page!.Jobs, j => j.JobId == job.JobId);
    }

    [Fact]
    public void Concurrent_jobs_in_one_repository_get_separate_worktrees()
    {
        using var f = new JobFixture();
        using var source = new TempStateDir();
        Git(source.Path, "init");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        var jobs = Enumerable.Range(0, 16).Select(i => f.Accept().Execute(new SubmitJobRequest($"k{i}", "task", null, false)
        {
            Cwd = source.Path,
            Worktree = true,
        }).Job!).ToList();

        Parallel.ForEach(jobs, job => Assert.Null(JobWorktree.Prepare(f.Store.GetJob(job.JobId)!)));
        Assert.All(jobs, job => Assert.Equal("atf/job-" + job.JobId, Git(job.WorktreePath!, "rev-parse", "--abbrev-ref", "HEAD")));
    }

    [Fact]
    public async Task A_job_cancelled_while_waiting_for_another_jobs_checkout_creates_no_worktree_and_starts_no_agent()
    {
        using var f = new JobFixture();
        using var source = new TempStateDir();
        Git(source.Path, "init");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        var backend = new ScriptedBackend(r =>
        [
            new BackendEvidence.Session(r.Correlation, "session-1"),
            new BackendEvidence.Result(r.Correlation, "done"),
        ]);
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, f.Admission, catalog.Names);
        var slow = accept.Execute(new SubmitJobRequest("slow", "first", null, false) { Cwd = source.Path, Worktree = true }).Job!;
        var waiting = accept.Execute(new SubmitJobRequest("waiting", "second", null, false)
        {
            Cwd = source.Path,
            Worktree = true,
            TimeoutSeconds = 1,
        }).Job!;

        // The slow job's checkout holds the repository until the test releases it.
        var started = Path.Combine(source.Path, "slow-started");
        var release = Path.Combine(source.Path, "slow-release");
        Executable(Path.Combine(source.Path, ".git", "hooks", "post-checkout"),
            $"case \"$PWD\" in *{slow.JobId}*) touch '{started}'; while [ ! -f '{release}' ]; do sleep 0.05; done;; esac");

        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        // Preparation runs synchronously inside the attempt, so each attempt gets its own thread.
        var slowClaim = f.Store.BeginNextAttempt()!;
        var slowRun = Task.Run(() => dispatcher.RunAttemptAsync(slowClaim, CancellationToken.None), TestContext.Current.CancellationToken);
        try
        {
            var watch = Stopwatch.StartNew();
            while (!File.Exists(started))
            {
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "the slow checkout never started");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            var waitingClaim = f.Store.BeginNextAttempt()!;
            var waitingRun = Task.Run(() => dispatcher.RunAttemptAsync(waitingClaim, CancellationToken.None), TestContext.Current.CancellationToken);
            // Times out behind the held checkout; before the fix it waited for the release instead.
            await Task.WhenAny(waitingRun, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            File.WriteAllText(release, "");
            await waitingRun.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
        }
        finally
        {
            File.WriteAllText(release, "");
            await slowRun.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
        }

        Assert.Equal((JobStatus.Cancelled, "timeout"), (f.Store.GetJob(waiting.JobId)!.Status, f.Store.GetJob(waiting.JobId)!.ReasonCode));
        Assert.False(Directory.Exists(waiting.WorktreePath));
        Assert.Equal([slow.WorktreePath], backend.Started.Select(r => r.WorkingDirectory));
        Assert.Equal(JobStatus.Completed, f.Store.GetJob(slow.JobId)!.Status);
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

    [Fact]
    public async Task Git_output_already_in_the_pipe_is_kept_when_its_reader_runs_after_the_drain_grace()
    {
        using var source = new TempStateDir();
        Git(source.Path, "init");
        // A starved reader got to the pipe after the post-exit grace and lost git's whole stdout (CI: not_owned_path).
        JobWorktree.ReaderDelay.Value = TimeSpan.FromSeconds(1.5);

        var result = await JobWorktree.RunAsync(source.Path, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken,
            "rev-parse", "--absolute-git-dir");

        Assert.Equal(0, result?.ExitCode);
        Assert.EndsWith(".git", result!.Output.Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readers_cut_while_a_descendant_still_holds_the_pipes_are_released()
    {
        using var source = new TempStateDir();
        Git(source.Path, "init");
        var pidFile = Path.Combine(source.Path, "child.pid");
        var script = Script(source.Path, $"sleep 30 &\necho $! > '{pidFile}'\necho ready", WindowsDescendant(pidFile, 30));
        var readers = new List<Task>();
        JobWorktree.ReaderCompletions.Value = readers;
        try
        {
            var result = await JobWorktree.GitAsync(source.Path, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken,
                "-c", $"alias.atf-probe=!{script}", "atf-probe");

            Assert.Contains("ready", result);
            Assert.Equal(2, readers.Count);
            Assert.All(readers, r => Assert.True(r.IsCompleted, "a cut reader still holds its thread and pipe"));
            using var child = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile).Trim()));
            Assert.False(child.HasExited);
        }
        finally
        {
            KillRecorded(pidFile);
        }
    }

    [Fact]
    public async Task Deadline_before_the_readers_catch_up_is_no_result_rather_than_partial_success()
    {
        using var source = new TempStateDir();
        Git(source.Path, "init");
        JobWorktree.ReaderDelay.Value = TimeSpan.FromSeconds(3);

        var result = await JobWorktree.RunAsync(source.Path, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken,
            "rev-parse", "--absolute-git-dir");

        Assert.Null(result);
    }

    [Fact]
    public async Task Git_success_is_not_held_by_a_descendant_that_keeps_stdout_after_git_exits()
    {
        using var source = new TempStateDir();
        Git(source.Path, "init");
        var pidFile = Path.Combine(source.Path, "child.pid");
        // An alias runs with git's own stdout and stderr; the background child keeps both open after git exits.
        var script = Script(source.Path, $"sleep 10 &\necho $! > '{pidFile}'\necho ready", WindowsDescendant(pidFile, 10));
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await JobWorktree.GitAsync(source.Path, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken,
                "-c", $"alias.atf-probe=!{script}", "atf-probe");
            Assert.NotNull(result);
            Assert.Contains("ready", result);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"git drain took {watch.Elapsed}");
        }
        finally
        {
            KillRecorded(pidFile);
        }
    }

    [Fact]
    public async Task Git_success_is_not_held_by_a_hook_descendant_that_keeps_stderr_after_git_exits()
    {
        using var source = new TempStateDir();
        Git(source.Path, "init");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        var pidFile = Path.Combine(source.Path, "child.pid");
        // Git points a hook's stdout at its own stderr, so the hook's child holds git's stderr.
        Executable(Path.Combine(source.Path, ".git", "hooks", "pre-commit"), $"sleep 10 &\necho $! > '{pidFile}'\necho ready");
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await JobWorktree.RunAsync(source.Path, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken,
                "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "second");
            Assert.Equal(0, result?.ExitCode);
            Assert.Contains("ready", result!.Error);
            Assert.Equal("second", Git(source.Path, "log", "-1", "--format=%s"));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"git drain took {watch.Elapsed}");
        }
        finally
        {
            KillRecorded(pidFile);
        }
    }

    [Fact]
    public async Task Git_stderr_is_captured_and_returned_on_failure()
    {
        using var source = new TempStateDir();
        Git(source.Path, "init");

        var result = await JobWorktree.RunAsync(source.Path, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken,
            "rev-parse", "--verify", "refs/heads/missing");

        Assert.NotNull(result);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("fatal", JobWorktree.ErrorText(result));
        Assert.Null(await JobWorktree.GitAsync(source.Path, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken,
            "rev-parse", "--verify", "refs/heads/missing"));
    }

    static void KillRecorded(string pidFile)
    {
        if (!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid)) { return; }
        try
        {
            using var child = Process.GetProcessById(pid);
            if (!child.HasExited)
            {
                child.Kill();
                child.WaitForExit(5_000);
            }
        }
        catch (ArgumentException)
        {
            // The test-owned child already exited.
        }
    }

    [Fact]
    public async Task Git_cancellation_stops_a_running_command_and_its_descendant()
    {
        using var source = new TempStateDir();
        Git(source.Path, "init");
        var pidFile = Path.Combine(source.Path, "child.pid");
        var script = Script(source.Path, "sleep 10 &\nwait", WindowsDescendant(pidFile, 30, wait: true));
        using var cancel = new CancellationTokenSource();
        if (!OperatingSystem.IsWindows()) { cancel.CancelAfter(300); }
        var watch = Stopwatch.StartNew();
        var run = JobWorktree.GitAsync(source.Path, TimeSpan.FromSeconds(OperatingSystem.IsWindows() ? 30 : 5), cancel.Token,
            "-c", $"alias.atf-probe=!{script}", "atf-probe");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // PowerShell startup can exceed 300ms: prove a descendant exists before cancelling.
                while (!File.Exists(pidFile))
                {
                    Assert.False(run.IsCompleted, "git exited before its descendant started");
                    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "the descendant never started");
                    await Task.Delay(20, TestContext.Current.CancellationToken);
                }
                watch.Restart();
                cancel.Cancel();
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"git cancellation took {watch.Elapsed}");
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var child = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile).Trim()));
                    Assert.True(child.WaitForExit(5_000), "cancellation left the descendant running");
                }
                catch (ArgumentException)
                {
                    // Already killed and reaped.
                }
            }
        }
        finally
        {
            cancel.Cancel();
            try { await run; }
            catch (OperationCanceledException) { }
            KillRecorded(pidFile);
        }
    }

    [Fact]
    public async Task Git_drains_large_output_without_retaining_it_all()
    {
        using var source = new TempStateDir();
        Git(source.Path, "init");
        var data = Path.Combine(source.Path, "output");
        File.WriteAllText(data, new string('x', 2 * 1024 * 1024));
        var script = Script(source.Path, $"cat '{data}'", PowerShell($"[Console]::Write([IO.File]::ReadAllText({PowerShellLiteral(data)}))"));

        var output = await JobWorktree.GitAsync(source.Path, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken,
            "-c", $"alias.atf-probe=!{script}", "atf-probe");

        Assert.NotNull(output);
        Assert.Equal(1024 * 1024, output.Length);
    }

    [Fact]
    public async Task Worktree_add_waits_for_another_process_holding_the_repository_lock_and_runs_once_it_dies()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "flock(1) holder");
        var ct = TestContext.Current.CancellationToken;
        using var source = new TempStateDir();
        using var worktrees = new TempStateDir();
        Git(source.Path, "init");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        var sha = Git(source.Path, "rev-parse", "HEAD");
        var common = Git(source.Path, "rev-parse", "--path-format=absolute", "--git-common-dir");
        var path = Path.Combine(worktrees.Path, "job_held");

        // Another process (a second daemon) holds the lock and then dies without releasing it.
        using var holder = Process.Start(new ProcessStartInfo("flock", ["--exclusive", Path.Combine(common, WorktreeLock.FileName), "sleep", "60"]))!;
        try
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                try { WorktreeLock.Acquire(common, ct, TimeSpan.Zero).Dispose(); }
                catch (TimeoutException) { break; }
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "the holder never took the lock");
                await Task.Delay(50, ct);
            }

            var prepare = Task.Run(() => JobWorktree.Prepare(source.Path, path, "atf/job-held", sha, ct), ct);
            await Task.Delay(500, ct);
            Assert.False(prepare.IsCompleted);
            Assert.False(Directory.Exists(path));

            holder.Kill(entireProcessTree: true);
            Assert.True(await prepare.WaitAsync(Bounded.ScenarioDeadline, ct));
            Assert.Equal("atf/job-held", Git(path, "rev-parse", "--abbrev-ref", "HEAD"));
        }
        finally
        {
            if (!holder.HasExited) { holder.Kill(entireProcessTree: true); }
        }
    }

    [Fact]
    public async Task Repository_lock_wait_honours_cancellation_and_fails_at_its_timeout()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempStateDir();
        using (WorktreeLock.Acquire(dir.Path, ct))
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cancel.CancelAfter(200);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorktreeLock.AcquireAsync(dir.Path, cancel.Token));
            Assert.Throws<TimeoutException>(() => WorktreeLock.Acquire(dir.Path, ct, TimeSpan.FromMilliseconds(300)));
        }
        // Disposing the handle releases the lock at once.
        WorktreeLock.Acquire(dir.Path, ct, TimeSpan.Zero).Dispose();
    }

    [Fact]
    public void Temp_directory_cleanup_removes_nested_read_only_git_objects()
    {
        var source = new TempStateDir();
        try
        {
            var objects = Directory.CreateDirectory(Path.Combine(source.Path, ".git", "objects", "ab")).FullName;
            var path = Path.Combine(objects, "object");
            File.WriteAllText(path, "object");
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            source.Dispose();
            Assert.False(Directory.Exists(source.Path));
        }
        finally
        {
            source.Dispose();
        }
    }

    static string Script(string directory, string body, string windowsBody)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(directory, "git-probe.cmd"), "@echo off\r\n" + windowsBody + "\r\n");
            // Git aliases run in the repository root. A bare filename avoids nested cmd/sh path quoting.
            // Disable MSYS argument conversion so /d and /c reach cmd unchanged.
            return "MSYS_NO_PATHCONV=1 cmd.exe /d /c git-probe.cmd";
        }
        var path = Path.Combine(directory, "git-probe.sh");
        Executable(path, body);
        return path;
    }

    static string PowerShellLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    static string PowerShell(string body) =>
        "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand " +
        Convert.ToBase64String(Encoding.Unicode.GetBytes(body));

    static string WindowsDescendant(string pidFile, int seconds, bool wait = false)
    {
        var file = PowerShellLiteral(pidFile);
        // /b inherits both pipes without opening a window. The child records its own PID before sleeping.
        // EncodedCommand keeps PowerShell syntax and paths out of cmd's quoting/expansion rules.
        var child = PowerShell($"[IO.File]::WriteAllText({file} + '.tmp', [string]$PID); " +
            $"[IO.File]::Move({file} + '.tmp', {file}); Start-Sleep -Seconds {seconds}");
        var ready = PowerShell($"$limit = [DateTime]::UtcNow.AddSeconds(10); " +
            $"while (!(Test-Path -LiteralPath {file})) {{ " +
            "if ([DateTime]::UtcNow -gt $limit) { exit 1 }; Start-Sleep -Milliseconds 20 }");
        return $"start \"\" /b {child}\r\n{ready}\r\nif errorlevel 1 exit /b 1\r\n" +
            (wait ? PowerShell($"Wait-Process -Id ([int][IO.File]::ReadAllText({file}))") : "echo ready");
    }

    static void Executable(string path, string body)
    {
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        // Git for Windows runs shebang hooks with its bundled sh; Windows has no Unix execute bit.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    static (BackendCatalog Catalog, AcceptJob Accept) Fake(JobFixture f)
    {
        var backend = new ScriptedBackend(r =>
        [
            new BackendEvidence.Session(r.Correlation, r.ResumeSessionId ?? "session-1"),
            new BackendEvidence.Result(r.Correlation, "done"),
        ]);
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        return (catalog, new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, f.Admission, catalog.Names));
    }

    static async Task Dispatch(JobFixture f, BackendCatalog catalog, Action<string>? log = null)
    {
        using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), log ?? (_ => { }));
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
