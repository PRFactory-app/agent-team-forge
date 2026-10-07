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

        Parallel.ForEach(jobs, job => Assert.True(JobWorktree.Prepare(f.Store.GetJob(job.JobId)!)));
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
