using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;
using AgentTeamForge.Host.Features.Jobs;
using AgentTeamForge.Host.Features.WebConsole;
using AgentTeamForge.Host.Transport;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class NativeCodexDeliveryTests
{
    static BackendCatalog Catalog(IJobBackend backend) => new BackendCatalog().Register(BackendCatalog.Codex, () => backend);

    static AcceptJob Accept(JobFixture fixture, BackendCatalog catalog) =>
        new(fixture.Store, JobFixture.Operator, fixture.Limits, fixture.TestProfile, fixture.Admission, catalog.Names);

    [Fact]
    public async Task Live_Codex_report_then_turn_end_starts_durable_follow_up()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ATF_REAL_TURN_QUEUE") == "1", "set ATF_REAL_TURN_QUEUE=1 for the isolated live probe");
        using var rig = new SpikeRig();
        var (exit, _, _) = await rig.RunToExitAsync(["init", "--state-dir", rig.StateDir]);
        Assert.Equal(0, exit);
        using var port = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        port.Start();
        var webPort = ((System.Net.IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        File.WriteAllText(Path.Combine(rig.StateDir, "launch-mode.json"), $$"""{"mode":"herdr","web_port":{{webPort}}}""");
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(Path.Combine(rig.StateDir, "launch-mode.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        var codexHome = Path.Combine(rig.StateDir, "scratch-codex");
        Directory.CreateDirectory(codexHome);
        var sourceHome = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        File.Copy(Path.Combine(sourceHome, "auth.json"), Path.Combine(codexHome, "auth.json"));
        File.WriteAllText(Path.Combine(codexHome, "config.toml"), "check_for_update_on_startup = false\n");
        try { await rig.StartDaemonWithEnvironmentAsync(new Dictionary<string, string> { ["CODEX_HOME"] = codexHome }); }
        catch (Exception error) { throw new InvalidOperationException(string.Join("\n", rig.DaemonLog), error); }
        var (_, lead) = await rig.StartBridgeAsync("turn-queue-scratch-lead", environment: new Dictionary<string, string> { ["CODEX_THREAD_ID"] = "" });
        var root = await SpikeRig.CallAsync(lead, "submit_job", new()
        {
            ["backend"] = "codex",
            ["model"] = "gpt-6.1-sol",
            ["effort"] = "high",
            ["cwd"] = rig.StateDir,
            ["idempotency_key"] = "live-root",
            ["name"] = "turn-queue-probe",
            ["instruction"] = "This is a scratch integration probe. Run sleep 8 with your shell tool, then discover the AgentTeamForge send_message tool if necessary and call mcp__agentteamforge__send_message(to=\"team-lead\", text=\"TURN_QUEUE_FIRST_REPORT\"). Immediately finish your turn with a short final reply. Do no other work."
        });
        Assert.True(root.Ok, root.ErrorDetail);
        var rootId = root.Job!.JobId;
        string? nextId = null;
        try
        {
            await Bounded.Until<IpcResponse>(async () =>
            {
                var job = await rig.GetAsync(rootId);
                return job.Job?.SessionId is not null ? job : null;
            }, "scratch Codex session binding", TimeSpan.FromMinutes(2));
            var next = await SpikeRig.CallAsync(lead, "follow_up", new()
            {
                ["job_id"] = rootId,
                ["idempotency_key"] = "live-next",
                ["defer"] = true,
                ["instruction"] = "Discover the AgentTeamForge send_message tool if necessary and call mcp__agentteamforge__send_message(to=\"team-lead\", text=\"TURN_QUEUE_SECOND_REPORT\"), then immediately finish your turn with a short final reply. Do no other work."
            });
            Assert.True(next.Ok, next.ErrorDetail);
            nextId = next.Job!.JobId;
            var firstDone = await Bounded.Until<IpcResponse>(async () =>
            {
                var job = await rig.GetAsync(rootId);
                return job.Job?.Status == JobStatus.Completed ? job : null;
            }, "live first turn completion", TimeSpan.FromMinutes(3));
            var secondDone = await Bounded.Until<IpcResponse>(async () =>
            {
                var job = await rig.GetAsync(nextId);
                return job.Job?.Status == JobStatus.Completed ? job : null;
            }, "live next turn completion", TimeSpan.FromMinutes(3));
            Assert.Equal(firstDone.Job!.SessionId, secondDone.Job!.SessionId);
            var inbox = await SpikeRig.CallAsync(lead, "read_messages", new() { ["full"] = true });
            var serialized = JsonSerializer.Serialize(inbox, IpcJson.Default.IpcResponse);
            var output = Environment.GetEnvironmentVariable("ATF_LIVE_TURN_QUEUE_EVIDENCE");
            if (output is not null)
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(firstDone, IpcJson.Default.IpcResponse) + "\n" + JsonSerializer.Serialize(secondDone, IpcJson.Default.IpcResponse) + "\n" + serialized);
                File.WriteAllLines(Path.Combine(output, "daemon.log"), rig.DaemonLog);
                foreach (var log in Directory.EnumerateFiles(Path.Combine(rig.StateDir, "logs"))) { File.Copy(log, Path.Combine(output, Path.GetFileName(log)), true); }
                foreach (var rollout in Directory.EnumerateFiles(Path.Combine(codexHome, "sessions"), "*.jsonl", SearchOption.AllDirectories)) { File.Copy(rollout, Path.Combine(output, Path.GetFileName(rollout)), true); }
            }
            Assert.Collection(inbox.Messages!,
                message => Assert.Equal("TURN_QUEUE_FIRST_REPORT", message.Text),
                message => Assert.Equal("TURN_QUEUE_SECOND_REPORT", message.Text));
        }
        finally
        {
            if (nextId is not null) { await rig.ClientAsync("stop", "--job", nextId); }
            await rig.ClientAsync("stop", "--job", rootId);
            await rig.ClientAsync("stop-agent", "--job", rootId);
        }
    }

    [Fact]
    public void Deferred_follow_ups_addressing_root_and_queued_tail_keep_durable_order()
    {
        using var fixture = new JobFixture();
        var catalog = Catalog(new ScriptedBackend(_ => []));
        var parent = Accept(fixture, catalog).Execute(new SubmitJobRequest("chain-root", "root", null, false)
        { Backend = BackendCatalog.Codex }).Job!;
        var root = fixture.Store.BeginNextAttempt()!;
        var rootRun = new RunRef(parent.JobId, root.RunId, root.Generation, root.Correlation);
        fixture.Store.RecordSession(rootRun, "thread-chain");
        var follow = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog));
        var first = follow.Execute(new FollowUpRequest(parent.JobId, "first", "chain-1") { Defer = true }).Job!;
        var active = fixture.Store.BeginNativeCodexAttempt(_ => true, Path.GetTempPath())!;
        var second = follow.Execute(new FollowUpRequest(first.JobId, "second", "chain-2") { Defer = true });
        Assert.Equal("accepted", second.Outcome);
        var thirdRequest = new FollowUpRequest(parent.JobId, "third", "chain-3") { Defer = true };
        var third = follow.Execute(thirdRequest);
        Assert.Equal("accepted", third.Outcome);
        var fourth = follow.Execute(new FollowUpRequest(second.Job!.JobId, "fourth", "chain-4") { Defer = true });
        Assert.Equal("accepted", fourth.Outcome);
        Assert.Equal(JobErrors.ParentNotReady, follow.Execute(new FollowUpRequest(first.JobId, "refused", "chain-no") { Defer = false }).Error);
        Assert.Equal(third.Job!.JobId, follow.Execute(thirdRequest).Job!.JobId);
        fixture.Store.Complete(rootRun, "root done");
        var restarted = fixture.NewStore();
        Assert.Null(restarted.BeginNativeCodexAttempt(_ => true, Path.GetTempPath()));
        Assert.True(restarted.SettleNativeAttempt(first.JobId, active.Correlation, "first done"));
        foreach (var job in new[] { second.Job!, third.Job!, fourth.Job! })
        {
            var claim = restarted.BeginNativeCodexAttempt(_ => true, Path.GetTempPath())!;
            Assert.Equal(job.JobId, claim.Job.JobId);
            Assert.Null(restarted.BeginNativeCodexAttempt(_ => true, Path.GetTempPath()));
            Assert.True(restarted.SettleNativeAttempt(job.JobId, claim.Correlation, "done"));
            Assert.False(restarted.SettleNativeAttempt(job.JobId, claim.Correlation, "duplicate"));
        }
        Assert.Null(restarted.BeginNextAttempt());
    }

    [Fact]
    public async Task Busy_codex_follow_up_is_claimed_without_ending_the_parent_and_fenced_once()
    {
        using var fixture = new JobFixture();
        var backend = new ScriptedBackend(request => [new BackendEvidence.Session(request.Correlation, "thread-busy")]) { Hangs = true };
        var catalog = Catalog(backend);
        var parent = Accept(fixture, catalog).Execute(new SubmitJobRequest("parent", "work", null, false)
        { Backend = BackendCatalog.Codex, TargetAgent = "sameagent" }).Job!;
        using var dispatcher = new DispatchJob(fixture.Store, catalog, fixture.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        var parentRun = dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        await Bounded.Until(() => fixture.Store.GetJob(parent.JobId)?.SessionId == "thread-busy", "session binding");

        var follow = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog));
        var child = follow.Execute(new FollowUpRequest(parent.JobId, "next", "next") { Defer = true }).Job!;
        Assert.Contains(";native_codex=1", fixture.Store.GetJob(child.JobId)!.Options);
        Assert.Null(fixture.Store.BeginNativeCodexAttempt(_ => true, "/tmp/codex-home", _ => false));
        var native = fixture.Store.BeginNativeCodexAttempt(_ => true, "/tmp/codex-home")!;
        Assert.Equal(child.JobId, native.Job.JobId);
        Assert.Equal(JobStatus.Running, fixture.Store.GetJob(parent.JobId)!.Status);
        Assert.Null(fixture.Store.BeginNativeCodexAttempt(_ => true, "/tmp/codex-home"));
        Assert.Equal("accepted", follow.Execute(new FollowUpRequest(parent.JobId, "later carrier", "other") { Defer = true }).Outcome);
        Assert.Equal("existing", follow.Execute(new FollowUpRequest(parent.JobId, "next", "next") { Defer = true }).Outcome);

        dispatcher.CancelRunning(parent.JobId);
        await parentRun.WaitAsync(Bounded.ScenarioDeadline, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Idle_codex_records_submission_and_restart_keeps_fence_until_receipt()
    {
        using var fixture = new JobFixture();
        var backend = new ScriptedBackend(request =>
        [
            new BackendEvidence.Session(request.Correlation, "thread-idle"),
            new BackendEvidence.Result(request.Correlation, "done")
        ]);
        var catalog = Catalog(backend);
        var parent = Accept(fixture, catalog).Execute(new SubmitJobRequest("parent", "work", null, false)
        { Backend = BackendCatalog.Codex, TargetAgent = "sameagent" }).Job!;
        using (var dispatcher = new DispatchJob(fixture.Store, catalog, fixture.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { }))
        {
            await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        }
        var follow = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog));
        var child = follow.Execute(new FollowUpRequest(parent.JobId, "next", "next")).Job!;
        var claim = fixture.Store.BeginNativeCodexAttempt(_ => true, "/tmp/codex-home")!;
        fixture.Store.RecordNativeSubmission(child.JobId, claim.Correlation, "submission-1");
        Assert.Equal("submission-1", fixture.Get().Execute(child.JobId).Job!.Delivery!.NativeSubmissionId);

        var restarted = fixture.NewStore();
        restarted.QuarantineUncertainAttempts();
        Assert.Equal("submission-1", restarted.NativeAttempt(child.JobId)!.SubmissionId);
        Assert.Equal(JobErrors.ParentNotReady,
            new FollowUpJob(restarted, JobFixture.Operator, fixture.Accept(restarted))
                .Execute(new FollowUpRequest(parent.JobId, "again", "another")).Error);
        Assert.Equal(JobErrors.ParentNotReady, Accept(fixture, catalog).Execute(new SubmitJobRequest("replacement", "work", null, false)
        { Backend = BackendCatalog.Codex, TargetAgent = "sameagent" }).Error);
        Assert.True(restarted.SettleNativeAttempt(child.JobId, claim.Correlation, "receipt"));
        Assert.False(restarted.SettleNativeAttempt(child.JobId, claim.Correlation, "duplicate"));
        restarted.RecordNativeSubmission(child.JobId, claim.Correlation, "late-submission");
        Assert.Equal("receipt", restarted.GetJob(child.JobId)!.ResultText);
        Assert.Equal("submission-1", restarted.NativeSubmissionId(child.JobId));
    }

    [Fact]
    public async Task Native_user_receipt_acknowledges_delivery_but_holds_queue_until_turn_end()
    {
        using var fixture = new JobFixture();
        var backend = new ScriptedBackend(request =>
        [
            new BackendEvidence.Session(request.Correlation, "thread-receipt"),
            new BackendEvidence.Result(request.Correlation, "done")
        ]);
        var catalog = Catalog(backend);
        var parent = Accept(fixture, catalog).Execute(new SubmitJobRequest("parent", "work", null, false) { Backend = BackendCatalog.Codex }).Job!;
        using var dispatcher = new DispatchJob(fixture.Store, catalog, fixture.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        var follow = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog));
        var first = follow.Execute(new FollowUpRequest(parent.JobId, "first", "first")).Job!;
        var claim = fixture.Store.BeginNativeCodexAttempt(_ => true, "/tmp/codex-home")!;
        fixture.Store.RecordNativeReceipt(first.JobId, claim.Correlation);
        Assert.Equal(JobStatus.Running, fixture.Store.GetJob(first.JobId)!.Status);
        Assert.True(fixture.Store.GetRuns(first.JobId).Single().Acked);
        var second = follow.Execute(new FollowUpRequest(parent.JobId, "second", "second") { Defer = true }).Job!;
        Assert.Null(fixture.Store.BeginNativeCodexAttempt(_ => true, "/tmp/codex-home"));
        Assert.True(fixture.Store.SettleNativeAttempt(first.JobId, claim.Correlation, "first result"));
        Assert.Equal(second.JobId, fixture.Store.BeginNativeCodexAttempt(_ => true, "/tmp/codex-home")!.Job.JobId);
        Assert.Equal("first result", fixture.Store.GetJob(first.JobId)!.ResultText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_native_completion_cannot_resettle_an_interrupted_job(bool receiptRecorded)
    {
        var (fixture, catalog, parent, _) = await CompletedParent("thread-cancelled");
        using var _fixture = fixture;
        var child = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog))
            .Execute(new FollowUpRequest(parent.JobId, "next", "next")).Job!;
        var claim = fixture.Store.BeginNativeCodexAttempt(_ => true, "/tmp/codex-home")!;
        if (receiptRecorded) { fixture.Store.RecordNativeReceipt(child.JobId, claim.Correlation); }

        fixture.Store.Cancel(child.JobId, JobFixture.Operator.Principal, JobFixture.Operator.Team, interrupt: true);
        Assert.False(fixture.Store.SettleNativeAttempt(child.JobId, claim.Correlation, "late result"));

        var job = fixture.Store.GetJob(child.JobId)!;
        Assert.Equal((JobStatus.Cancelled, "interrupted", (string?)null), (job.Status, job.ReasonCode, job.ResultText));
        Assert.Equal("cancelled", fixture.Store.GetRuns(child.JobId).Single().State);
        Assert.Equal(receiptRecorded ? "received" : "sent", fixture.Store.NativeAttempt(child.JobId)!.State);
        Assert.DoesNotContain(fixture.Store.GetEvents(child.JobId), e => e.Kind == JobStatus.Completed);
    }

    [Fact]
    public async Task Large_codex_follow_up_uses_resume()
    {
        using var fixture = new JobFixture();
        var backend = new ScriptedBackend(request =>
        [
            new BackendEvidence.Session(request.Correlation, "thread-large"),
            new BackendEvidence.Result(request.Correlation, "done")
        ]);
        var catalog = Catalog(backend);
        var parent = Accept(fixture, catalog).Execute(new SubmitJobRequest("parent", "work", null, false) { Backend = BackendCatalog.Codex }).Job!;
        using var dispatcher = new DispatchJob(fixture.Store, catalog, fixture.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        var child = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog))
            .Execute(new FollowUpRequest(parent.JobId, new string('x', 17_000), "large")).Job!;
        Assert.DoesNotContain("native_codex", fixture.Store.GetJob(child.JobId)!.Options);
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        Assert.Equal("thread-large", backend.Started[1].ResumeSessionId);
    }

    [Fact]
    public async Task Unverifiable_codex_thread_falls_back_to_resume()
    {
        using var fixture = new JobFixture();
        var backend = new ScriptedBackend(request =>
        [
            new BackendEvidence.Session(request.Correlation, "thread-dead"),
            new BackendEvidence.Result(request.Correlation, "done")
        ]);
        var catalog = Catalog(backend);
        var parent = Accept(fixture, catalog).Execute(new SubmitJobRequest("parent", "work", null, false) { Backend = BackendCatalog.Codex }).Job!;
        using var dispatcher = new DispatchJob(fixture.Store, catalog, fixture.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        var child = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog))
            .Execute(new FollowUpRequest(parent.JobId, "next", "dead")).Job!;
        Assert.Null(fixture.Store.BeginNativeCodexAttempt(_ => false, "/tmp/codex-home"));
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        Assert.Equal("thread-dead", backend.Started[1].ResumeSessionId);
        Assert.Null(fixture.Store.NativeSubmissionId(child.JobId));
    }

    static async Task<(JobFixture Fixture, BackendCatalog Catalog, JobView Parent, ScriptedBackend Backend)> CompletedParent(string thread)
    {
        var fixture = new JobFixture();
        var backend = new ScriptedBackend(request =>
        [
            new BackendEvidence.Session(request.Correlation, thread),
            new BackendEvidence.Result(request.Correlation, "done")
        ]);
        var catalog = Catalog(backend);
        var parent = Accept(fixture, catalog).Execute(new SubmitJobRequest("parent", "work", null, false)
        { Backend = BackendCatalog.Codex, TargetAgent = "sameagent" }).Job!;
        using var dispatcher = new DispatchJob(fixture.Store, catalog, fixture.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { });
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        return (fixture, catalog, parent, backend);
    }

    [Fact]
    public async Task Stop_releases_an_unresolved_native_fence_without_resending()
    {
        var (fixture, catalog, parent, backend) = await CompletedParent("thread-stuck");
        using var _fixture = fixture;
        var follow = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog));
        var child = follow.Execute(new FollowUpRequest(parent.JobId, "next", "next")).Job!;
        var submits = 0;
        using var logDir = new TempStateDir();
        var jobLogs = new JobLogs(logDir.Path);
        var daemonLog = new List<string>();
        const string diagnostic = "codex queue exit_code=7 timeout=False stdout_tail=started stderr_tail=rejected";
        using var dispatcher = new DispatchJob(fixture.Store, catalog, fixture.Limits, DurabilityCheckpoints.None, new AdmissionGate(), daemonLog.Add, jobLogs)
        {
            SubmitNativeCodex = (_, _, _, _) => { submits++; return Task.FromResult(new CodexSubmission(true, null, diagnostic)); }
        };
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNativeCodexAttempt(_ => true, "/tmp/codex-home")!, CancellationToken.None);
        Assert.Equal(JobStatus.NeedsReconciliation, fixture.Store.GetJob(child.JobId)!.Status);
        Assert.Contains(diagnostic, jobLogs.Read(child.JobId).Text);
        Assert.Contains(jobLogs.ReadActivity(child.JobId, BackendCatalog.Codex).Entries, entry => entry.Text == diagnostic);
        Assert.Contains(daemonLog, line => line.Contains(child.JobId, StringComparison.Ordinal) && line.Contains(diagnostic, StringComparison.Ordinal));
        var refused = follow.Execute(new FollowUpRequest(parent.JobId, "retry", "retry"));
        Assert.Equal(JobErrors.ParentNotReady, refused.Error);
        var endpoint = new JobsEndpoint(Accept(fixture, catalog), fixture.Get(), follow,
            fixture.List(), new StopJob(fixture.Store, JobFixture.Operator, dispatcher.CancelRunning, releaseNative: dispatcher.ReleaseNative),
            DurabilityCheckpoints.None, () => { });
        var response = endpoint.Handle(new IpcRequest { Op = IpcProtocol.JobFollowUp, JobId = parent.JobId, Instruction = "retry", IdempotencyKey = "retry" });
        var mcp = JobsMcpBridge.ToolResult(response);
        Assert.True(mcp.IsError);
        using var wire = JsonDocument.Parse(mcp.StructuredContent!.Value.GetRawText());
        Assert.Equal(child.JobId, wire.RootElement.GetProperty("fencing_job_id").GetString());
        var recovery = wire.RootElement.GetProperty("recovery");
        Assert.Equal("stop_job", recovery.GetProperty("tool").GetString());
        Assert.Equal(child.JobId, recovery.GetProperty("arguments").GetProperty("job_id").GetString());

        Assert.True(fixture.Get().Execute(child.JobId).Job!.ReleaseAvailable);
        Assert.True(fixture.List().Execute(new ListJobsRequest()).Page!.Jobs.Single(j => j.JobId == child.JobId).ReleaseAvailable);
        var token = WebConsoleServer.NewToken();
        await using var console = await WebConsoleServer.StartAsync(0, token, (request, _) => Task.FromResult(endpoint.Handle(request)));
        using var http = new HttpClient();
        using var release = new HttpRequestMessage(HttpMethod.Post, console.Url + "api/jobs/" + child.JobId + "/release");
        release.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        release.Headers.Add("Origin", console.Url.TrimEnd('/'));
        using var released = await http.SendAsync(release, TestContext.Current.CancellationToken);
        released.EnsureSuccessStatusCode();
        Assert.Equal("stopped", fixture.Store.GetJob(child.JobId)!.ReasonCode);
        Assert.Equal(JobStatus.Cancelled, fixture.Store.GetJob(child.JobId)!.Status);
        Assert.False(fixture.Store.IsSessionFenced(child.JobId));
        Assert.Equal("released", fixture.Store.NativeAttempt(child.JobId)!.State);
        Assert.Empty(fixture.Store.UnresolvedNativeAttempts());
        Assert.Equal(JobErrors.NativeReleaseUnavailable, endpoint.Handle(new IpcRequest { Op = IpcProtocol.JobRelease, JobId = child.JobId }).Error);
        Assert.NotNull(follow.Execute(new FollowUpRequest(parent.JobId, "retry", "retry")).Job);
        Assert.Equal(1, submits);
        Assert.Equal(0, backend.Terminations);
    }

    [Fact]
    public async Task Codex_queue_that_never_started_reverts_to_resume()
    {
        var (fixture, catalog, parent, backend) = await CompletedParent("thread-nocodex");
        using var _fixture = fixture;
        var child = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog))
            .Execute(new FollowUpRequest(parent.JobId, "next", "next")).Job!;
        using var dispatcher = new DispatchJob(fixture.Store, catalog, fixture.Limits, DurabilityCheckpoints.None, new AdmissionGate(), _ => { })
        {
            SubmitNativeCodex = (_, _, _, _) => Task.FromResult(new CodexSubmission(false, null))
        };
        await dispatcher.RunAttemptAsync(fixture.Store.BeginNativeCodexAttempt(_ => true, "/tmp/codex-home")!, CancellationToken.None);
        var reverted = fixture.Store.GetJob(child.JobId)!;
        Assert.Equal(JobStatus.Queued, reverted.Status);
        Assert.DoesNotContain("native_codex", reverted.Options);
        Assert.Empty(fixture.Store.UnresolvedNativeAttempts());

        await dispatcher.RunAttemptAsync(fixture.Store.BeginNextAttempt()!, CancellationToken.None);
        Assert.Equal("thread-nocodex", backend.Started[1].ResumeSessionId);
        Assert.Equal(JobStatus.Completed, fixture.Store.GetJob(child.JobId)!.Status);
    }

    [Fact]
    public async Task Ordinary_resume_waits_for_an_unresolved_native_attempt_on_the_thread()
    {
        var (fixture, catalog, parent, _) = await CompletedParent("thread-order");
        using var _fixture = fixture;
        var follow = new FollowUpJob(fixture.Store, JobFixture.Operator, Accept(fixture, catalog));
        var large = follow.Execute(new FollowUpRequest(parent.JobId, new string('x', 17_000), "large")).Job!;
        var native = follow.Execute(new FollowUpRequest(parent.JobId, "next", "next")).Job!;
        var claim = fixture.Store.BeginNativeCodexAttempt(_ => true, "/tmp/codex-home")!;
        Assert.Equal(native.JobId, claim.Job.JobId);
        // An interrupt of the native job clears its own session fence; N5 must still hold.
        fixture.Store.Cancel(native.JobId, JobFixture.Operator.Principal, JobFixture.Operator.Team, interrupt: true);
        fixture.Store.ReconcileStoppedJob(native.JobId);
        Assert.Null(fixture.Store.BeginNextAttempt());

        fixture.Store.RecordNativeReceipt(native.JobId, claim.Correlation);
        Assert.Null(fixture.Store.BeginNextAttempt());
        Assert.True(fixture.Store.ReleaseNativeAttempt(native.JobId, JobFixture.Operator.Principal, JobFixture.Operator.Team).Changed);
        Assert.Equal(large.JobId, fixture.Store.BeginNextAttempt()!.Job.JobId);
    }
}
