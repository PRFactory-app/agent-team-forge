using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.External;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.External;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Features.Sessions;
using AgentTeamForge.DAL.Features.Wake;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class SafetyModeTests
{
    static readonly string[] AllBackends = [BackendCatalog.Fake, BackendCatalog.Claude, BackendCatalog.Codex, BackendCatalog.Pi, BackendCatalog.Cursor, BackendCatalog.Droid];

    /// <summary>Claude and Codex enforce, as on a verified Linux machine; every other backend gets the real default (no sandbox).</summary>
    static string? Enforcing(string backend, string? cwd, string? launchMode) => backend is BackendCatalog.Claude or BackendCatalog.Codex or BackendCatalog.Fake ? null : SandboxCapability.Default(backend, cwd, launchMode);

    static AcceptJob Accept(JobFixture f, SandboxProbe? probe = null, string? defaultMode = null) =>
        new(f.Store, JobFixture.Operator, f.Limits, f.TestProfile, f.Admission, AllBackends, sandboxProbe: probe ?? Enforcing, defaultSafetyMode: defaultMode);

    static JobSafety Safety(JobFixture f, string jobId) => JobSafety.Read(f.Store.GetJob(jobId)!.Options);

    [Fact]
    public void Full_access_is_the_default_and_stores_no_mode_keys()
    {
        using var f = new JobFixture();

        var job = Accept(f).Execute(new SubmitJobRequest("k", "task", null, false)).Job!;

        Assert.DoesNotContain("sandbox", f.Store.GetJob(job.JobId)!.Options);
        Assert.Equal(SafetyMode.FullAccess, job.RequestedSafetyMode);
        Assert.Equal(SafetyMode.FullAccess, job.EffectiveSafetyMode);
        Assert.Null(job.SafetyDowngradeReason);
    }

    [Fact]
    public void Sandboxed_request_stores_requested_and_effective_mode_in_the_options()
    {
        using var f = new JobFixture();

        var job = Accept(f).Execute(new SubmitJobRequest("k", "task", null, false) { Backend = BackendCatalog.Claude, SafetyMode = SafetyMode.Sandboxed }).Job!;

        Assert.Contains(";sandbox=1;sandbox_effective=1", f.Store.GetJob(job.JobId)!.Options);
        Assert.Equal(SafetyMode.Sandboxed, job.RequestedSafetyMode);
        Assert.Equal(SafetyMode.Sandboxed, job.EffectiveSafetyMode);
        Assert.Null(job.SafetyDowngradeReason);
        Assert.Equal(SafetyMode.Sandboxed, f.Get().Execute(job.JobId).Job!.EffectiveSafetyMode);
    }

    [Theory]
    [InlineData(BackendCatalog.Pi)]
    [InlineData(BackendCatalog.Cursor)]
    [InlineData(BackendCatalog.Droid)]
    public void Backends_without_a_sandbox_run_full_access_and_record_the_downgrade(string backend)
    {
        using var f = new JobFixture();

        var result = Accept(f).Execute(new SubmitJobRequest("k", "task", null, false) { Backend = backend, SafetyMode = SafetyMode.Sandboxed });

        Assert.Null(result.Error);
        var safety = Safety(f, result.Job!.JobId);
        Assert.Equal(new JobSafety(SafetyMode.Sandboxed, SafetyMode.FullAccess, SafetyDowngrade.UnsupportedBackend), safety);
        Assert.Equal(SafetyMode.Sandboxed, result.Job.RequestedSafetyMode);
        Assert.Equal(SafetyMode.FullAccess, result.Job.EffectiveSafetyMode);
        Assert.Equal("sandbox_unsupported_backend", result.Job.SafetyDowngradeReason);
    }

    [Fact]
    public void A_machine_that_cannot_enforce_records_sandbox_unavailable()
    {
        using var f = new JobFixture();

        var job = Accept(f, (_, _, _) => SafetyDowngrade.Unavailable).Execute(new SubmitJobRequest("k", "task", null, false) { Backend = BackendCatalog.Claude, SafetyMode = SafetyMode.Sandboxed }).Job!;

        Assert.Equal(new JobSafety(SafetyMode.Sandboxed, SafetyMode.FullAccess, "sandbox_unavailable"), Safety(f, job.JobId));
    }

    [Fact]
    public void Profile_default_applies_when_a_request_names_no_mode_and_an_explicit_mode_wins()
    {
        using var f = new JobFixture();
        var accept = Accept(f, defaultMode: SafetyMode.Sandboxed);

        var inherited = accept.Execute(new SubmitJobRequest("a", "task", null, false) { Backend = BackendCatalog.Claude }).Job!;
        var explicitFull = accept.Execute(new SubmitJobRequest("b", "task", null, false) { Backend = BackendCatalog.Claude, SafetyMode = SafetyMode.FullAccess }).Job!;

        Assert.Equal(SafetyMode.Sandboxed, inherited.RequestedSafetyMode);
        Assert.Equal(SafetyMode.FullAccess, explicitFull.RequestedSafetyMode);
    }

    [Fact]
    public void An_unknown_safety_mode_is_rejected()
    {
        using var f = new JobFixture();

        var result = Accept(f).Execute(new SubmitJobRequest("k", "task", null, false) { SafetyMode = "bypass" });

        Assert.Equal(JobErrors.InvalidRequest, result.Error);
    }

    [Fact]
    public void A_job_started_by_a_sandboxed_agent_cannot_lower_the_mode()
    {
        using var f = new JobFixture();

        var child = Accept(f).Execute(new SubmitJobRequest("k", "task", null, false)
        { Backend = BackendCatalog.Claude, SafetyMode = SafetyMode.FullAccess, InheritedSafetyMode = SafetyMode.Sandboxed }).Job!;

        Assert.Equal(SafetyMode.Sandboxed, child.RequestedSafetyMode);
        Assert.Equal(SafetyMode.Sandboxed, child.EffectiveSafetyMode);
    }

    [Fact]
    public void Submit_over_mcp_by_a_managed_child_of_a_sandboxed_job_inherits_sandboxed_and_a_full_access_parent_adds_nothing()
    {
        using var f = new JobFixture();
        var accept = Accept(f);
        var sessions = new LeadSessionStore(f.Database);
        var endpoint = new JobsEndpoint(accept, f.Get(), new FollowUpJob(f.Store, JobFixture.Operator, accept), f.List(),
            new StopJob(f.Store, JobFixture.Operator, _ => { }), new DurabilityCheckpoints(null), () => { }, jobStore: f.Store, sessions: sessions,
            external: new ExternalTeam(new ExternalMemberStore(f.Database), new WakeStore(f.Database)));
        var workspace = Path.GetDirectoryName(f.DatabasePath)!;
        var sandboxedParent = accept.Execute(new SubmitJobRequest("p1", "task", null, false) { Backend = BackendCatalog.Claude, SafetyMode = SafetyMode.Sandboxed }).Job!;
        var openParent = accept.Execute(new SubmitJobRequest("p2", "task", null, false) { Backend = BackendCatalog.Claude }).Job!;

        IpcResponse SubmitAs(string parentJobId, string key, string? mode)
        {
            var lead = sessions.Start(workspace, "managed-child:" + parentJobId);
            return endpoint.Handle(new IpcRequest
            {
                Op = IpcProtocol.JobSubmit,
                IdempotencyKey = key,
                Instruction = "child",
                Backend = BackendCatalog.Pi,
                LeadSessionId = lead.SessionId,
                Workspace = workspace,
                SafetyMode = mode,
            });
        }

        var lowered = SubmitAs(sandboxedParent.JobId, "c1", SafetyMode.FullAccess);
        var open = SubmitAs(openParent.JobId, "c2", null);

        Assert.True(lowered.Ok, lowered.Error);
        Assert.Equal(SafetyMode.Sandboxed, lowered.Job!.RequestedSafetyMode);
        // Pi has no sandbox: the inherited request is downgraded and says so (accepted containment limit).
        Assert.Equal(SafetyMode.FullAccess, lowered.Job.EffectiveSafetyMode);
        Assert.Equal(SafetyDowngrade.UnsupportedBackend, lowered.Job.SafetyDowngradeReason);
        Assert.Equal(SafetyMode.FullAccess, open.Job!.RequestedSafetyMode);
    }

    [Theory]
    [InlineData(SafetyMode.Sandboxed, null)]
    [InlineData(SafetyMode.Sandboxed, SafetyDowngrade.Unavailable)]
    [InlineData(SafetyMode.FullAccess, null)]
    public async Task Follow_up_and_revive_keep_the_mode_of_the_parent(string mode, string? reason)
    {
        using var f = new JobFixture();
        var accept = Accept(f, (_, _, _) => reason);
        var backend = new ScriptedBackend(r => [new BackendEvidence.Session(r.Correlation, r.ResumeSessionId ?? "s-" + r.JobId), new BackendEvidence.Result(r.Correlation, "ok")]);
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        async Task RunNext()
        {
            using var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
            await dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        }
        var parent = accept.Execute(new SubmitJobRequest("first", "first", null, false) { SafetyMode = mode }).Job!;
        await RunNext();
        var followUp = new FollowUpJob(f.Store, JobFixture.Operator, accept);

        var secondResult = followUp.Execute(new FollowUpRequest(parent.JobId, "second", "second"));
        Assert.True(secondResult.Error is null, secondResult.Error + secondResult.Detail);
        await RunNext();
        // The prf-revive and revive_agent paths are follow-ups of a finished parent: the chain keeps the mode too.
        var thirdResult = followUp.Execute(new FollowUpRequest(secondResult.Job!.JobId, "third", "third"));
        Assert.True(thirdResult.Error is null, thirdResult.Error + thirdResult.Detail);

        Assert.Equal(Safety(f, parent.JobId), Safety(f, secondResult.Job.JobId));
        Assert.Equal(Safety(f, parent.JobId), Safety(f, thirdResult.Job!.JobId));
        Assert.Equal(mode, thirdResult.Job.RequestedSafetyMode);
        Assert.Equal(reason is null ? mode : SafetyMode.FullAccess, thirdResult.Job.EffectiveSafetyMode);
        Assert.Equal(reason, thirdResult.Job.SafetyDowngradeReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fake_backend_scenario_launches_sandboxed_in_a_worktree_with_the_stored_options(bool networkOptOut)
    {
        using var f = new JobFixture();
        using var source = new TempStateDir();
        Git(source.Path, "init");
        Git(source.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
        var backend = new ScriptedBackend(r => [new BackendEvidence.Session(r.Correlation, "s-1"), new BackendEvidence.Result(r.Correlation, "ok")]);
        var catalog = new BackendCatalog().Register(BackendCatalog.Fake, () => backend);
        var job = Accept(f).Execute(new SubmitJobRequest("k", "task", null, false) { Cwd = source.Path, Worktree = true, SafetyMode = SafetyMode.Sandboxed }).Job!;

        using (var dispatcher = new DispatchJob(f.Store, catalog, f.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { },
            stateRoot: Path.GetDirectoryName(f.DatabasePath))
        { SandboxSettings = () => SandboxSettings.Default with { Network = !networkOptOut } })
        {
            await dispatcher.RunAttemptAsync(f.Store.BeginNextAttempt()!, CancellationToken.None);
        }

        Assert.Equal(JobStatus.Completed, f.Store.GetJob(job.JobId)!.Status);
        var started = Assert.Single(backend.Started);
        Assert.Contains(";sandbox=1;sandbox_effective=1", started.Options);
        Assert.NotNull(started.Sandbox);
        Assert.Equal(Path.Combine(source.Path, ".git"), started.Sandbox.GitCommonDir);
        Assert.Contains(Path.Combine(source.Path, ".git", "worktrees"), started.Sandbox.GitDir);
        Assert.Null(SandboxedWorktrees.Validate(job.WorktreePath!));
        Assert.Equal(!networkOptOut, started.Sandbox.Network); // Network on unless the machine's profile opts out.
    }

    [Fact]
    public void A_downgraded_job_launches_without_a_sandbox()
    {
        using var f = new JobFixture();
        var job = Accept(f, (_, _, _) => SafetyDowngrade.Unavailable).Execute(new SubmitJobRequest("k", "task", null, false) { SafetyMode = SafetyMode.Sandboxed }).Job!;

        Assert.False(JobSafety.Read(f.Store.GetJob(job.JobId)!.Options).EffectiveSandboxed);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("sandboxed", true)]
    [InlineData("fullAccess", true)]
    [InlineData("bypass", false)]
    public void Profile_default_must_be_a_known_mode(string? mode, bool valid)
    {
        using var dir = new TempStateDir();
        var path = dir.File("state");
        Assert.Equal(0, AgentTeamForge.Host.Features.Setup.InitCommand.Run(path, testProfile: true, queueLimit: null, maxRuntimeSeconds: null));
        var state = AgentTeamForge.Host.Hosting.StateDirectory.Open(path);
        var profile = ProfileFile.Load(state) with { SafetyMode = mode };
        File.WriteAllText(state.ProfileFile, System.Text.Json.JsonSerializer.Serialize(profile, SetupJson.Default.ProfileFile));

        if (valid) { Assert.Equal(mode, ProfileFile.Load(state).SafetyMode); }
        else { Assert.Throws<AgentTeamForge.Host.Hosting.StateDirectoryException>(() => ProfileFile.Load(state)); }
    }

    static string Git(string cwd, params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(cwd);
        foreach (var arg in args) { info.ArgumentList.Add(arg); }
        using var process = System.Diagnostics.Process.Start(info)!;
        Assert.True(process.WaitForExit(10_000));
        var output = process.StandardOutput.ReadToEnd().Trim();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        return output;
    }
}
