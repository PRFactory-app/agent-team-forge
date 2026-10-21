using System.Diagnostics;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class SandboxedWorktreeTests : IDisposable
{
    readonly TempStateDir _dir = new();
    readonly string _repo;
    readonly string _worktree;
    readonly string _admin;

    public SandboxedWorktreeTests()
    {
        _repo = Directory.CreateDirectory(_dir.File("repo")).FullName;
        Git(_repo, "init");
        Git(_repo, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        _worktree = _dir.File("wt");
        Git(_repo, "worktree", "add", "-b", "atf/job-1", _worktree);
        _admin = Path.Combine(_repo, ".git", "worktrees", "wt");
        Assert.True(SandboxedWorktrees.RegisterFromOrigin(_worktree, _repo));
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task An_untouched_worktree_validates_and_daemon_git_runs_hardened()
    {
        Assert.Null(SandboxedWorktrees.Validate(_worktree));
        // A planted hook would run on a normal commit; the daemon's git ignores it.
        var hooks = Path.Combine(_repo, ".git", "hooks");
        File.WriteAllText(Path.Combine(hooks, "post-checkout"), $"#!/bin/sh\ntouch '{_dir.File("hook-ran")}'\n");
        File.SetUnixFileMode(Path.Combine(hooks, "post-checkout"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var branch = await JobWorktree.GitAsync(_worktree, TimeSpan.FromSeconds(10), CancellationToken.None, "checkout", "-b", "other");

        Assert.NotNull(branch);
        Assert.False(File.Exists(_dir.File("hook-ran")));
    }

    [Fact]
    public async Task A_hook_runs_in_a_worktree_that_is_not_registered()
    {
        var plain = _dir.File("plain");
        Git(_repo, "worktree", "add", "-b", "plain", plain);
        var hook = Path.Combine(_repo, ".git", "hooks", "post-checkout");
        File.WriteAllText(hook, $"#!/bin/sh\ntouch '{_dir.File("hook-ran")}'\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await JobWorktree.GitAsync(plain, TimeSpan.FromSeconds(10), CancellationToken.None, "checkout", "-b", "other");

        Assert.True(File.Exists(_dir.File("hook-ran")));
    }

    [Fact]
    public async Task A_redirected_commondir_fails_validation_and_daemon_git_refuses_to_run()
    {
        var fake = Directory.CreateDirectory(_dir.File("fake-common")).FullName;
        File.WriteAllText(Path.Combine(_admin, "commondir"), fake + "\n");

        Assert.Equal("common_dir_changed", SandboxedWorktrees.Validate(_worktree));
        Assert.Null(await JobWorktree.GitAsync(_worktree, TimeSpan.FromSeconds(10), CancellationToken.None, "rev-parse", "HEAD"));
    }

    [Fact]
    public void A_config_worktree_file_fails_validation()
    {
        File.WriteAllText(Path.Combine(_admin, "config.worktree"), "[core]\n\tfsmonitor = /tmp/evil\n");

        Assert.Equal("config_worktree_present", SandboxedWorktrees.Validate(_worktree));
    }

    [Fact]
    public void A_symlink_in_the_admin_dir_fails_validation()
    {
        File.CreateSymbolicLink(Path.Combine(_admin, "stray"), "/etc/passwd");

        Assert.Equal("symlink_in_git_dir", SandboxedWorktrees.Validate(_worktree));
    }

    [Fact]
    public void A_git_file_pointing_at_another_admin_dir_fails_validation()
    {
        var other = Directory.CreateDirectory(_dir.File("other-admin")).FullName;
        File.WriteAllText(Path.Combine(_worktree, ".git"), "gitdir: " + other + "\n");

        Assert.Equal("admin_dir_moved", SandboxedWorktrees.Validate(_worktree));
    }

    [Fact]
    public void A_git_file_replaced_by_a_symlink_fails_validation()
    {
        File.Delete(Path.Combine(_worktree, ".git"));
        File.CreateSymbolicLink(Path.Combine(_worktree, ".git"), _admin);

        Assert.Equal("git_is_link", SandboxedWorktrees.Validate(_worktree));
    }

    [Fact]
    public async Task A_tampered_sandboxed_job_fails_instead_of_completing()
    {
        using var f = new JobFixture();
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, f.Admission, [BackendCatalog.Fake], sandboxProbe: (_, _) => null);
        var job = accept.Execute(new SubmitJobRequest("k", "task", null, false) { Cwd = _repo, Worktree = true, SafetyMode = SafetyMode.Sandboxed }).Job!;
        var backend = new ScriptedBackend(r =>
        {
            // The agent rewrites the admin dir of its own worktree while it runs.
            var admin = Directory.GetDirectories(Path.Combine(_repo, ".git", "worktrees")).Single(d => File.ReadAllText(Path.Combine(d, "gitdir")).Contains(job.JobId, StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(admin, "config.worktree"), "[core]\n\thooksPath = /tmp\n");
            return [new BackendEvidence.Session(r.Correlation, "s-1"), new BackendEvidence.Result(r.Correlation, "ok")];
        });
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);

        using (var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { }))
        {
            await dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        }

        var stored = f.Store.GetJob(job.JobId)!;
        Assert.Equal(JobStatus.Failed, stored.Status);
        Assert.Equal(SandboxedWorktrees.TamperedReason, stored.ReasonCode);
    }

    [Fact]
    public void Restore_registers_the_worktrees_of_sandboxed_jobs_from_the_store()
    {
        using var f = new JobFixture();
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, f.Admission, [BackendCatalog.Fake], sandboxProbe: (_, _) => null);
        var job = accept.Execute(new SubmitJobRequest("k", "task", null, false) { Cwd = _repo, Worktree = true, SafetyMode = SafetyMode.Sandboxed }).Job!;
        Directory.CreateDirectory(job.WorktreePath!);

        SandboxedWorktrees.Restore(f.Store);

        // No .git in the empty directory: a registered path fails validation, an unregistered one would be null.
        Assert.Equal("git_missing", SandboxedWorktrees.Validate(job.WorktreePath!));
    }

    static string Git(string cwd, params string[] args)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(cwd);
        foreach (var arg in args) { info.ArgumentList.Add(arg); }
        using var process = Process.Start(info)!;
        Assert.True(process.WaitForExit(10_000));
        var output = process.StandardOutput.ReadToEnd().Trim();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        return output;
    }
}
