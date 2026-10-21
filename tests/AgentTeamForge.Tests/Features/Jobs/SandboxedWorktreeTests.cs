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
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, f.Admission, [BackendCatalog.Fake], sandboxProbe: (_, _, _) => null);
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
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, f.Admission, [BackendCatalog.Fake], sandboxProbe: (_, _, _) => null);
        var job = accept.Execute(new SubmitJobRequest("k", "task", null, false) { Cwd = _repo, Worktree = true, SafetyMode = SafetyMode.Sandboxed }).Job!;
        Directory.CreateDirectory(Path.GetDirectoryName(job.WorktreePath!)!);
        Git(_repo, "worktree", "add", "-b", "restored", job.WorktreePath!);
        SandboxedWorktrees.Forget(job.WorktreePath!);

        SandboxedWorktrees.Restore(f.Store);

        File.WriteAllText(Path.Combine(_repo, ".git", "worktrees", Path.GetFileName(job.WorktreePath!), "commondir"), _dir.Path + "\n");
        Assert.Equal("common_dir_changed", SandboxedWorktrees.Validate(job.WorktreePath!));
    }

    [Fact]
    public async Task Daemon_git_is_pinned_to_the_recorded_dirs_so_a_swap_after_validation_is_never_read()
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        SandboxedWorktrees.ApplyGuard(info, _worktree, "rev-parse");
        // The sandboxed process is still running: it redirects the common dir between the validation and the git run.
        var evil = _dir.File("evil.git");
        Git(_dir.Path, "init", "--bare", evil);
        File.WriteAllText(Path.Combine(_admin, "commondir"), evil + "\n");
        foreach (var argument in new[] { "-C", _worktree, "rev-parse", "--path-format=absolute", "--git-common-dir" }) { info.ArgumentList.Add(argument); }

        using var process = Process.Start(info)!;
        var output = (await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken)).Trim();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_repo, ".git"), output);
    }

    [Fact]
    public async Task Daemon_git_refuses_to_run_while_an_agent_of_the_worktree_is_alive()
    {
        var root = SandboxedWorktrees.MarkLive(_worktree);
        Assert.NotNull(root);
        try { Assert.Null(await JobWorktree.GitAsync(_worktree, TimeSpan.FromSeconds(10), CancellationToken.None, "rev-parse", "HEAD")); }
        finally { SandboxedWorktrees.ClearLive(root); }

        Assert.NotNull(await JobWorktree.GitAsync(_worktree, TimeSpan.FromSeconds(10), CancellationToken.None, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task A_filter_driver_naming_a_workspace_script_does_not_run_in_daemon_git()
    {
        var marker = _dir.File("filter-ran");
        var script = Path.Combine(_worktree, "evil.sh");
        File.WriteAllText(script, $"#!/bin/sh\ntouch '{marker}'\ncat\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(_worktree, ".gitattributes"), "data.txt filter=evil\n");
        File.WriteAllText(Path.Combine(_worktree, "data.txt"), "x");
        Git(_repo, "config", "filter.evil.clean", script);
        Git(_repo, "config", "diff.evil.textconv", script);

        Assert.NotNull(await JobWorktree.GitAsync(_worktree, TimeSpan.FromSeconds(10), CancellationToken.None, "add", "data.txt"));
        Assert.False(File.Exists(marker), "the driver ran under the daemon's guarded git");

        // Control: the same command without the guard runs the driver, so the assertion above has teeth.
        SandboxedWorktrees.Forget(_worktree);
        Git(_worktree, "add", "--renormalize", "data.txt");
        Assert.True(File.Exists(marker));
    }

    string[] Roots => [_worktree, _admin, Path.Combine(_repo, ".git")];

    string RealLfs()
    {
        var bin = Directory.CreateDirectory(_dir.File("bin")).FullName;
        var lfs = Path.Combine(bin, "git-lfs");
        File.WriteAllText(lfs, "#!/bin/sh\n");
        File.SetUnixFileMode(lfs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return lfs;
    }

    static string LfsConfig(string executable) =>
        $"filter.lfs.clean\n{executable} clean -- %f\0filter.lfs.smudge\n{executable} smudge -- %f\0filter.lfs.process\n{executable} filter-process\0filter.lfs.required\ntrue\0";

    [Fact]
    public void Only_the_real_lfs_filter_survives_and_every_other_driver_is_blanked()
    {
        var lfs = RealLfs();
        var bin = Path.GetDirectoryName(lfs)!;

        // Kept, and pinned to the resolved path so the shell never searches PATH again.
        foreach (var overrides in new[] { SandboxedWorktrees.Overrides(LfsConfig(lfs), Roots, "/usr/bin"), SandboxedWorktrees.Overrides(LfsConfig("git-lfs"), Roots, bin) })
        {
            Assert.DoesNotContain("filter.lfs.clean=", overrides);
            Assert.Contains($"filter.lfs.clean={Path.GetFullPath(lfs)} clean -- %f", overrides);
            Assert.Contains($"filter.lfs.process={Path.GetFullPath(lfs)} filter-process", overrides);
        }
        string[] expected = ["filter.x.clean=", "filter.x.smudge=", "filter.x.process=", "filter.x.required=false", "diff.y.textconv=", "diff.y.command=", "merge.m.driver="];
        var other = SandboxedWorktrees.Overrides("filter.x.smudge\n/usr/bin/cat\0diff.y.textconv\n/ws/t\0diff.y.command\nz\0merge.m.driver\nq\0core.editor\nvi\0", Roots, bin);
        Assert.Equal(expected.Order(), other.Where(a => a != "-c").Order());
    }

    [Fact]
    public void An_lfs_command_that_is_relative_shadowed_inside_the_workspace_or_off_grammar_is_blanked()
    {
        var lfs = RealLfs();
        var bin = Path.GetDirectoryName(lfs)!;
        var inside = Path.Combine(_worktree, "git-lfs");
        File.WriteAllText(inside, "#!/bin/sh\n");
        var linked = Directory.CreateDirectory(_dir.File("linkbin")).FullName;
        File.CreateSymbolicLink(Path.Combine(linked, "git-lfs"), inside);

        Assert.Contains("filter.lfs.clean=", SandboxedWorktrees.Overrides(LfsConfig("./git-lfs"), Roots, bin));
        Assert.Contains("filter.lfs.clean=", SandboxedWorktrees.Overrides(LfsConfig("git-lfs"), Roots, "." + Path.PathSeparator + bin));
        Assert.Contains("filter.lfs.clean=", SandboxedWorktrees.Overrides(LfsConfig("git-lfs"), Roots, _worktree + Path.PathSeparator + bin));
        Assert.Contains("filter.lfs.clean=", SandboxedWorktrees.Overrides(LfsConfig(inside), Roots, bin));
        // An outside path whose real target is inside the workspace.
        Assert.Contains("filter.lfs.clean=", SandboxedWorktrees.Overrides(LfsConfig(Path.Combine(linked, "git-lfs")), Roots, bin));
        Assert.Contains("filter.lfs.clean=", SandboxedWorktrees.Overrides($"filter.lfs.clean\n{lfs} clean -- %f; touch /tmp/x\0", Roots, bin));
        Assert.Contains("filter.lfs.clean=", SandboxedWorktrees.Overrides($"filter.lfs.clean\n{lfs} clean %f\0", Roots, bin));
        Assert.Contains("filter.lfs.clean=", SandboxedWorktrees.Overrides($"filter.lfs.clean\n\"{lfs}\" clean -- %f\0", Roots, bin));
    }

    [Fact]
    public void A_safe_lfs_definition_in_an_inactive_include_does_not_hide_an_unsafe_active_one_in_either_order()
    {
        var lfs = RealLfs();
        var bin = Path.GetDirectoryName(lfs)!;
        var unsafeDefinition = $"filter.lfs.clean\n{Path.Combine(_worktree, "evil.sh")}\0";
        var safeDefinition = $"filter.lfs.clean\n{lfs} clean -- %f\0";

        Assert.Contains("filter.lfs.clean=", SandboxedWorktrees.Overrides(unsafeDefinition + safeDefinition, Roots, bin));
        Assert.Contains("filter.lfs.clean=", SandboxedWorktrees.Overrides(safeDefinition + unsafeDefinition, Roots, bin));
    }

    [Fact]
    public void A_non_executable_git_lfs_earlier_on_path_does_not_hide_the_workspace_one_the_shell_would_run()
    {
        var lfs = RealLfs();
        var decoy = Directory.CreateDirectory(_dir.File("decoy")).FullName;
        File.WriteAllText(Path.Combine(decoy, "git-lfs"), "not executable");
        File.SetUnixFileMode(Path.Combine(decoy, "git-lfs"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var workspaceBin = Directory.CreateDirectory(Path.Combine(_worktree, "bin")).FullName;
        File.Copy(lfs, Path.Combine(workspaceBin, "git-lfs"));
        File.SetUnixFileMode(Path.Combine(workspaceBin, "git-lfs"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var path = string.Join(Path.PathSeparator, decoy, workspaceBin, Path.GetDirectoryName(lfs));

        Assert.Contains("filter.lfs.clean=", SandboxedWorktrees.Overrides(LfsConfig("git-lfs"), Roots, path));
        // Without the workspace entry the non-executable decoy is skipped and the real one is found.
        Assert.DoesNotContain("filter.lfs.clean=", SandboxedWorktrees.Overrides(LfsConfig("git-lfs"), Roots, decoy + Path.PathSeparator + Path.GetDirectoryName(lfs)));
    }

    [Fact]
    public async Task An_unsafe_active_lfs_driver_is_blank_even_with_a_safe_definition_in_an_inactive_include()
    {
        var marker = _dir.File("lfs-ran");
        var script = ArmFilter("evil.sh", marker);
        File.WriteAllText(Path.Combine(_worktree, ".gitattributes"), "data.txt filter=lfs\n");
        var lfs = RealLfs();
        var include = _dir.File("lfs.inc");
        File.WriteAllText(include, $"[filter \"lfs\"]\n\tclean = {lfs} clean -- %f\n");
        Git(_repo, "config", "filter.lfs.clean", script);
        File.AppendAllText(Path.Combine(_repo, ".git", "config"), $"[includeIf \"onbranch:never-matches\"]\n\tpath = {include}\n");

        Assert.NotNull(await JobWorktree.GitAsync(_worktree, TimeSpan.FromSeconds(10), CancellationToken.None, "add", "data.txt"));
        Assert.False(File.Exists(marker), "the active workspace LFS command ran");

        SandboxedWorktrees.Forget(_worktree);
        Git(_worktree, "add", "--renormalize", "data.txt");
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task A_workspace_of_the_filesystem_root_overlaps_the_state_dir_at_submit_and_at_dispatch()
    {
        using var f = new JobFixture();
        var state = Directory.CreateDirectory(_dir.File("state")).FullName;
        Assert.True(SandboxLaunch.OverlapsState(state, "/"));
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, f.Admission, [BackendCatalog.Fake], sandboxProbe: (_, _, _) => null, stateRoot: state);

        var submitted = accept.Execute(new SubmitJobRequest("root", "t", null, false) { Cwd = "/", SafetyMode = SafetyMode.Sandboxed }).Job!;
        Assert.Equal((SafetyMode.FullAccess, SafetyDowngrade.Unavailable), (submitted.EffectiveSafetyMode, submitted.SafetyDowngradeReason));

        var unguarded = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, f.Admission, [BackendCatalog.Fake], sandboxProbe: (_, _, _) => null);
        var stored = unguarded.Execute(new SubmitJobRequest("root2", "t", null, false) { Cwd = "/", SafetyMode = SafetyMode.Sandboxed }).Job!;
        Assert.Equal(SafetyMode.Sandboxed, stored.EffectiveSafetyMode);
        var backend = new ScriptedBackend(r => [new BackendEvidence.Result(r.Correlation, "ok")]);
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        using (var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { }, stateRoot: state))
        {
            // The downgraded submit runs first, as full access; the stored sandboxed one must not launch.
            await dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
            await dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        }

        Assert.Single(backend.Started);
        Assert.Null(backend.Started[0].Sandbox);
        Assert.Equal("sandbox_state_overlap", f.Store.GetJob(stored.JobId)!.ReasonCode);
    }

    string ArmFilter(string name, string marker)
    {
        var script = Path.Combine(_worktree, name);
        File.WriteAllText(script, $"#!/bin/sh\ntouch '{marker}'\ncat\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(_worktree, ".gitattributes"), "data.txt filter=evil\n");
        File.WriteAllText(Path.Combine(_worktree, "data.txt"), "x");
        return script;
    }

    [Fact]
    public async Task A_workspace_relative_lfs_command_does_not_run_in_daemon_git()
    {
        var marker = _dir.File("lfs-ran");
        ArmFilter("git-lfs", marker);
        File.WriteAllText(Path.Combine(_worktree, ".gitattributes"), "data.txt filter=lfs\n");
        Git(_repo, "config", "filter.lfs.clean", "./git-lfs clean -- %f");
        Git(_repo, "config", "filter.lfs.smudge", "./git-lfs smudge -- %f");

        Assert.NotNull(await JobWorktree.GitAsync(_worktree, TimeSpan.FromSeconds(10), CancellationToken.None, "add", "data.txt"));
        Assert.False(File.Exists(marker), "the workspace executable ran as git-lfs");

        SandboxedWorktrees.Forget(_worktree);
        Git(_worktree, "add", "--renormalize", "data.txt");
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task A_driver_that_a_branch_switch_turns_on_through_includeif_is_blank_before_the_switch_and_after()
    {
        var marker = _dir.File("filter-ran");
        var script = ArmFilter("evil.sh", marker);
        var include = _dir.File("evil.inc");
        File.WriteAllText(include, $"[filter \"evil\"]\n\tclean = {script}\n");
        File.AppendAllText(Path.Combine(_repo, ".git", "config"), $"[includeIf \"onbranch:evilbranch\"]\n\tpath = {include}\n");
        // The agent switches HEAD (its own git is unguarded); only then does the include apply.
        Git(_worktree, "checkout", "-b", "evilbranch");

        Assert.NotNull(await JobWorktree.GitAsync(_worktree, TimeSpan.FromSeconds(10), CancellationToken.None, "add", "data.txt"));
        Assert.False(File.Exists(marker), "the conditional include activated a workspace driver");

        SandboxedWorktrees.Forget(_worktree);
        Git(_worktree, "add", "--renormalize", "data.txt");
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task Driver_overrides_are_recomputed_for_every_invocation()
    {
        var marker = _dir.File("filter-ran");
        var script = ArmFilter("evil.sh", marker);
        Assert.NotNull(await JobWorktree.GitAsync(_worktree, TimeSpan.FromSeconds(10), CancellationToken.None, "add", "data.txt"));
        // The config gains a driver after the first guarded run (an operator edit, or an include that now matches).
        Git(_repo, "config", "filter.evil.clean", script);

        Assert.NotNull(await JobWorktree.GitAsync(_worktree, TimeSpan.FromSeconds(10), CancellationToken.None, "add", "--renormalize", "data.txt"));

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task A_config_include_inside_the_workspace_makes_daemon_git_refuse()
    {
        var include = Path.Combine(_worktree, "evil.inc");
        File.WriteAllText(include, "[core]\n");
        File.AppendAllText(Path.Combine(_repo, ".git", "config"), $"[include]\n\tpath = {include}\n");

        Assert.Null(await JobWorktree.GitAsync(_worktree, TimeSpan.FromSeconds(10), CancellationToken.None, "rev-parse", "HEAD"));
    }

    [Fact]
    public void A_writable_root_that_contains_or_reaches_into_the_state_dir_overlaps_it_but_a_job_worktree_does_not()
    {
        var state = Directory.CreateDirectory(_dir.File("state")).FullName;
        Directory.CreateDirectory(Path.Combine(state, "worktrees", "job-1"));
        Directory.CreateDirectory(Path.Combine(state, "sandbox"));
        var elsewhere = Directory.CreateDirectory(_dir.File("elsewhere")).FullName;

        Assert.True(SandboxLaunch.OverlapsState(state, _dir.Path));
        Assert.True(SandboxLaunch.OverlapsState(state, state));
        Assert.True(SandboxLaunch.OverlapsState(state, Path.Combine(state, "sandbox")));
        Assert.True(SandboxLaunch.OverlapsState(state, Path.Combine(state, "worktrees")));
        Assert.False(SandboxLaunch.OverlapsState(state, Path.Combine(state, "worktrees", "job-1")));
        Assert.False(SandboxLaunch.OverlapsState(state, elsewhere));
        Assert.False(SandboxLaunch.OverlapsState(null, _dir.Path));
    }

    [Fact]
    public void A_sandboxed_submit_with_cwd_around_the_state_dir_is_downgraded_with_a_reason()
    {
        using var f = new JobFixture();
        var state = Directory.CreateDirectory(_dir.File("state")).FullName;
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, f.Admission, [BackendCatalog.Fake], sandboxProbe: (_, _, _) => null, stateRoot: state);

        var home = accept.Execute(new SubmitJobRequest("a", "t", null, false) { Cwd = _dir.Path, SafetyMode = SafetyMode.Sandboxed }).Job!;
        var inside = accept.Execute(new SubmitJobRequest("b", "t", null, false) { Cwd = state, SafetyMode = SafetyMode.Sandboxed }).Job!;
        var fine = accept.Execute(new SubmitJobRequest("c", "t", null, false) { Cwd = _repo, SafetyMode = SafetyMode.Sandboxed }).Job!;

        Assert.Equal((SafetyMode.FullAccess, SafetyDowngrade.Unavailable), (home.EffectiveSafetyMode, home.SafetyDowngradeReason));
        Assert.Equal((SafetyMode.FullAccess, SafetyDowngrade.Unavailable), (inside.EffectiveSafetyMode, inside.SafetyDowngradeReason));
        Assert.Equal(SafetyMode.Sandboxed, fine.EffectiveSafetyMode);
    }

    [Fact]
    public async Task A_stored_sandboxed_job_whose_writable_root_reaches_the_state_dir_fails_before_launch()
    {
        using var f = new JobFixture();
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, f.Admission, [BackendCatalog.Fake], sandboxProbe: (_, _, _) => null);
        var job = accept.Execute(new SubmitJobRequest("a", "t", null, false) { Cwd = _dir.Path, SafetyMode = SafetyMode.Sandboxed }).Job!;
        var backend = new ScriptedBackend(r => [new BackendEvidence.Result(r.Correlation, "ok")]);
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);

        using (var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { }, stateRoot: Directory.CreateDirectory(_dir.File("daemon-state")).FullName))
        {
            await dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        }

        Assert.Empty(backend.Started);
        Assert.Equal("sandbox_state_overlap", f.Store.GetJob(job.JobId)!.ReasonCode);
    }

    [Fact]
    public void A_working_directory_that_is_a_link_out_of_the_worktree_is_refused()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_worktree, "sub")).FullName;
        Assert.Equal(Path.GetFullPath(sub), SandboxedWorktrees.ContainedWorkingDirectory(_worktree, sub));
        Directory.Delete(sub);
        var outside = Directory.CreateDirectory(_dir.File("home")).FullName;
        Directory.CreateSymbolicLink(sub, outside);

        Assert.Null(SandboxedWorktrees.ContainedWorkingDirectory(_worktree, sub));
        Assert.Null(SandboxedWorktrees.ContainedWorkingDirectory(_worktree, outside));
    }

    [Fact]
    public async Task A_cold_follow_up_does_not_launch_when_a_subdirectory_became_a_link_to_the_home_directory()
    {
        using var f = new JobFixture();
        var sub = Directory.CreateDirectory(Path.Combine(_repo, "sub")).FullName;
        File.WriteAllText(Path.Combine(sub, "tracked.txt"), "x");
        Git(_repo, "add", "-A");
        Git(_repo, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "sub");
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, f.Admission, [BackendCatalog.Fake], sandboxProbe: (_, _, _) => null);
        var backend = new ScriptedBackend(r => [new BackendEvidence.Session(r.Correlation, r.ResumeSessionId ?? "s-1"), new BackendEvidence.Result(r.Correlation, "ok")]);
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        async Task RunNext()
        {
            using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
            await dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        }
        var parent = accept.Execute(new SubmitJobRequest("k", "task", null, false) { Cwd = sub, Worktree = true, SafetyMode = SafetyMode.Sandboxed }).Job!;
        await RunNext();
        Assert.Equal(Path.Combine(parent.WorktreePath!, "sub"), backend.Started[0].WorkingDirectory);
        var inWorktree = Path.Combine(parent.WorktreePath!, "sub");
        Directory.Delete(inWorktree, recursive: true);
        Directory.CreateSymbolicLink(inWorktree, Directory.CreateDirectory(_dir.File("home")).FullName);

        var child = new FollowUpJob(f.Store, JobFixture.Operator, accept).Execute(new FollowUpRequest(parent.JobId, "again", "k2")).Job!;
        await RunNext();

        Assert.Single(backend.Started);
        Assert.Equal(JobStatus.Failed, f.Store.GetJob(child.JobId)!.Status);
        Assert.Equal(SandboxedWorktrees.TamperedReason, f.Store.GetJob(child.JobId)!.ReasonCode);
    }

    [Fact]
    public async Task A_sandboxed_job_in_an_existing_linked_worktree_is_registered_and_survives_a_restart()
    {
        using var f = new JobFixture();
        var existing = _dir.File("wt-existing");
        Git(_repo, "worktree", "add", "-b", "existing", existing);
        var admin = Path.Combine(_repo, ".git", "worktrees", "wt-existing");
        File.WriteAllText(Path.Combine(admin, "config.worktree"), "[core]\n");
        Assert.Null(SandboxedWorktrees.Validate(existing));
        File.Delete(Path.Combine(admin, "config.worktree"));
        var accept = new AcceptJob(f.Store, JobFixture.Operator, f.Limits, true, f.Admission, [BackendCatalog.Fake], sandboxProbe: (_, _, _) => null);
        var backend = new ScriptedBackend(r => [new BackendEvidence.Session(r.Correlation, "s-1"), new BackendEvidence.Result(r.Correlation, "ok")]);
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        accept.Execute(new SubmitJobRequest("k", "task", null, false) { Cwd = existing, Worktree = false, SafetyMode = SafetyMode.Sandboxed });

        using (var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { }))
        {
            await dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        }

        // The launch granted this admin dir as writable, so it is now recorded and checked.
        Assert.Equal(admin, backend.Started[0].Sandbox!.GitDir);
        File.WriteAllText(Path.Combine(admin, "config.worktree"), "[core]\n");
        Assert.Equal("config_worktree_present", SandboxedWorktrees.Validate(existing));
        File.Delete(Path.Combine(admin, "config.worktree"));
        var file = _dir.File("registry/worktrees.json");
        SandboxedWorktrees.SaveTo(file);
        SandboxedWorktrees.Forget(existing);
        Assert.Null(SandboxedWorktrees.Validate(existing));
        SandboxedWorktrees.LoadFrom(file);
        File.WriteAllText(Path.Combine(admin, "commondir"), _dir.Path + "\n");
        Assert.Equal("common_dir_changed", SandboxedWorktrees.Validate(existing));
    }

    [Fact]
    public void The_workspace_label_of_a_tampered_sandboxed_worktree_is_not_read_from_the_redirected_common_dir()
    {
        var evil = _dir.File("evil.git");
        Git(_dir.Path, "init", "--bare", evil);
        File.WriteAllText(Path.Combine(_admin, "commondir"), evil + "\n");

        Assert.Equal("wt", AgentTeamForge.Business.Features.Agents.Terminals.HerdrTerminal.WorkspaceLabel(_worktree));
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
