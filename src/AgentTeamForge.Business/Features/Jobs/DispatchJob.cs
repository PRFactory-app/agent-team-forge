using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>
/// Dispatcher: claims unattempted intents and runs up to
/// <see cref="SpikeLimits.MaxConcurrentJobs"/> attempts at once (the store keeps
/// turns on one native session serial). Each attempt commits its start before any
/// backend effect and turns backend evidence into exactly one fenced terminal
/// write. Driven by the daemon lifetime token only. One deadline bounds the
/// whole effect of an attempt: start, delivery and evidence reading.
/// </summary>
public sealed class DispatchJob : IDisposable
{
    /// <summary>Upper bound for a configured maximum runtime.</summary>
    public static readonly TimeSpan MaxAllowedRuntime = TimeSpan.FromHours(24);

    readonly SemaphoreSlim _signal = new(0);
    readonly CancellationTokenSource _halted = new();
    readonly ConcurrentDictionary<string, ActiveRun> _running = new();
    readonly ConcurrentDictionary<string, IBackendRun> _reconciledWindows = new();
    readonly SemaphoreSlim _claimGate = new(1, 1);
    readonly Lock _haltClaimGate = new();
    readonly JobStore store;
    readonly BackendCatalog backends;
    readonly SpikeLimits limits;
    readonly DurabilityCheckpoints checkpoints;
    readonly AdmissionGate admission;
    readonly Action<string> log;
    readonly JobLogs? jobLogs;
    readonly ManagedChildContext? childContext;
    readonly string? piHome;
    string? _haltReason;

    /// <summary>Single-backend convenience: serves jobs whose backend is "fake".</summary>
    public DispatchJob(JobStore store, IJobBackend backend, SpikeLimits limits, DurabilityCheckpoints checkpoints, AdmissionGate admission, Action<string> log, JobLogs? jobLogs = null)
        : this(store, new BackendCatalog().Register(BackendCatalog.Fake, () => backend), limits, checkpoints, admission, log, jobLogs)
    {
    }

    public DispatchJob(JobStore store, BackendCatalog backends, SpikeLimits limits, DurabilityCheckpoints checkpoints, AdmissionGate admission, Action<string> log, JobLogs? jobLogs = null, ManagedChildContext? childContext = null, string? piHome = null)
    {
        // Validated before the daemon reports readiness; CancelAfter would otherwise fault the loop.
        if (limits.MaxFakeRuntime <= TimeSpan.Zero || limits.MaxFakeRuntime > MaxAllowedRuntime)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), limits.MaxFakeRuntime, "max runtime must be positive and bounded");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxConcurrentJobs, 1, nameof(limits));

        this.store = store;
        this.backends = backends;
        this.limits = limits;
        this.checkpoints = checkpoints;
        this.admission = admission;
        this.log = log;
        this.jobLogs = jobLogs;
        this.childContext = childContext;
        this.piHome = piHome;
    }

    /// <summary>
    /// Why the dispatcher stopped claiming work (terminal_write_failed or
    /// dispatcher_fault); null while healthy. Setting it closes admission at the
    /// same instant and stops in-flight attempts as a shutdown would (left
    /// started); <see cref="RunAsync"/> then returns and the host exits
    /// unhealthy. Restart quarantines those runs.
    /// </summary>
    public string? HaltReason => Volatile.Read(ref _haltReason);

    public bool Halted => HaltReason is not null;

    public void Signal() => _signal.Release();

    /// <summary>Interrupts an active attempt after its cancelled state has committed.</summary>
    public void CancelRunning(string jobId)
    {
        if (_running.TryGetValue(jobId, out var active))
        {
            active.TerminateOnce(backend => TryTerminate(backend, jobId));
            try { active.Stop.Cancel(); }
            catch (ObjectDisposedException) { } // The attempt just finished.
        }
    }

    /// <summary>Stops a quarantined agent only through verified backend ownership.</summary>
    public bool StopReconciled(JobRecord job)
    {
        if (job.Status != JobStatus.NeedsReconciliation) { return false; }
        var backend = backends.Resolve(job.Backend);
        if (backend is HerdrInteractiveBackend herdr)
        {
            lock (herdr.SessionStopGate)
            {
                var peers = store.GetSessionJobs(job.JobId);
                if (!herdr.HasOwnedJobs(peers) || !herdr.StopOwnedJobs(peers)) { return false; }
                return true;
            }
        }
        if (backend is WtInteractiveBackend wt) { return wt.StopOwnedJob(job.JobId); }
        // While the attempt is still unwinding, its held Process is the only
        // trustworthy Windows ownership proof. Never reconstruct a handle from a PID.
        if (_running.TryGetValue(job.JobId, out var active) && active.BackendRun is { OwnedChildAlive: true }
            && active.TerminateOnce(run => run!.TerminateOwnedChild()))
        {
            try { active.Stop.Cancel(); } catch (ObjectDisposedException) { }
            return true;
        }
        if (_reconciledWindows.TryGetValue(job.JobId, out var retained) && retained.OwnedChildAlive)
        {
            retained.TerminateOwnedChild();
            return true;
        }
        var runs = store.GetRuns(job.JobId);
        if (runs.Count == 0 || !OrphanedBackendProcess.HasMarkedProcess([runs[^1].Correlation],
            runs[^1].BackendPid is int pid ? [pid] : [])) { return false; }
        OrphanedBackendProcess.TerminateMarked([runs[^1].Correlation]);
        return !OrphanedBackendProcess.HasMarkedProcess([runs[^1].Correlation],
            runs[^1].BackendPid is int knownPid ? [knownPid] : []);
    }

    public void ForgetReconciledOwnership(JobRecord job)
    {
        if (backends.Resolve(job.Backend) is HerdrInteractiveBackend herdr)
        {
            herdr.ForgetStoppedJobs(store.GetSessionJobs(job.JobId));
        }
        if (_reconciledWindows.TryRemove(job.JobId, out var retained))
        {
            _ = DisposeQuietly(retained);
        }
    }

    /// <summary>Stops the current turn while preserving an interactive agent's live tab.</summary>
    public void InterruptRunning(string jobId)
    {
        if (_running.TryGetValue(jobId, out var active))
        {
            // Own the stop effect before cancellation wakes RunAttemptAsync's
            // catch path, which otherwise wins and kills an interactive TUI.
            active.TerminateOnce(backend => TryInterrupt(backend, jobId));
            try { active.Stop.Cancel(); }
            catch (ObjectDisposedException) { }
            CloseInterruptedIfUnclaimed(jobId);
        }
        else
        {
            // The cancelled run may have finished cleanup after the acceptance
            // commit but before this callback. Headless descendants can outlive
            // that cleanup, so verify their run markers before releasing the fence.
            try
            {
                var job = store.GetJob(jobId);
                if (job?.Status != JobStatus.Cancelled) { return; }
                if (backends.Resolve(job.Backend) is not HerdrInteractiveBackend)
                {
                    var runs = store.GetRuns(jobId);
                    var correlations = runs.Select(run => run.Correlation).ToArray();
                    OrphanedBackendProcess.TerminateMarked(correlations);
                    if (OrphanedBackendProcess.HasMarkedProcess(correlations,
                        [.. runs.Where(run => run.BackendPid.HasValue).Select(run => run.BackendPid!.Value)])) { return; }
                }
                store.ReconcileStoppedJob(jobId);
            }
            catch (Exception ex)
            {
                log($"session reconciliation failed for {jobId}: {ex.Message}");
                Halt("terminal_write_failed");
            }
        }
    }

    internal void CloseInterruptedIfUnclaimed(string jobId)
    {
        // A stop can win after the child commit but before InterruptTurn remembers
        // its tab. The committed child state is the final arbiter.
        try
        {
            var parent = store.GetJob(jobId);
            if (!store.IsSessionFenced(jobId) && !store.HasQueuedFollowUp(jobId) && parent?.SessionId is { } sessionId &&
                backends.Resolve(parent.Backend) is HerdrInteractiveBackend interactive)
            {
                interactive.CloseUnclaimedSession(sessionId);
            }
        }
        catch (Exception ex)
        { log($"Herdr interrupt cleanup failed for {jobId}: {ex.Message}"); }
    }

    /// <summary>Releases a retained Herdr tab when its follow-up was cancelled in the queue.</summary>
    public void CloseUnclaimedFollowUp(string jobId)
    {
        try
        {
            var job = store.GetJob(jobId);
            // A sibling follow-up still queued on the same parent resumes in that tab.
            if (job?.ParentJobId is not { } parentId || store.IsSessionFenced(parentId) || store.HasQueuedFollowUp(parentId)
                || store.GetJob(parentId)?.SessionId is not { } sessionId)
            {
                return;
            }
            if (backends.Resolve(job.Backend) is HerdrInteractiveBackend interactive)
            {
                interactive.CloseUnclaimedSession(sessionId);
            }
        }
        catch (Exception ex) { log($"Herdr cleanup failed for {jobId}: {ex.Message}"); }
    }

    /// <summary>Commits a daemon-owned cancellation, then interrupts the running attempt.</summary>
    void CancelOwned(string jobId, string reason)
    {
        try
        {
            if (store.CancelOwned(jobId, reason).WasRunning)
            {
                CancelRunning(jobId);
            }
        }
        catch (StorageException ex)
        {
            // Left running; the runtime deadline still bounds the attempt.
            log($"{reason} cancel failed for {jobId}: {ex.Failure}");
        }
    }

    /// <summary>Keep a new submission out of the claim loop until its reply is sent.</summary>
    public IDisposable PauseClaims()
    {
        _claimGate.Wait();
        return new ClaimPause(_claimGate);
    }

    public void Dispose()
    {
        _signal.Dispose();
        _halted.Dispose();
        _claimGate.Dispose();
    }

    sealed class ClaimPause(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    /// <summary>Admission is closed whenever this returns or throws, halted or shut down.</summary>
    public async Task RunAsync(CancellationToken daemonLifetime)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(daemonLifetime, _halted.Token);
        using var slots = new SemaphoreSlim(limits.MaxConcurrentJobs);
        var inFlight = new List<Task>();
        var sweeping = SweepQueueAsync(stopping.Token);
        try
        {
            await ClaimLoopAsync(slots, inFlight, stopping.Token);
        }
        catch
        {
            Halt("dispatcher_fault");
            throw;
        }
        finally
        {
            try
            {
                // Attempts observe the stopping token; wait so none outlives the dispatcher.
                await Task.WhenAll([.. inFlight, sweeping]);
            }
            finally
            {
                admission.Close(HaltReason ?? "daemon_stopping");
            }
        }
    }

    /// <summary>
    /// Cancels queued jobs whose queue TTL passed, even while every slot is busy.
    /// The claim query also skips them, so an expired job never starts.
    /// </summary>
    async Task SweepQueueAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                SweepExpiredQueued();
            }
            catch (StorageException ex)
            {
                log($"queue ttl sweep failed: {ex.Failure}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal void SweepExpiredQueued()
    {
        foreach (var jobId in store.ExpireQueued())
        {
            log($"queue ttl expired for {jobId}");
            CloseUnclaimedFollowUp(jobId);
        }
    }

    async Task ClaimLoopAsync(SemaphoreSlim slots, List<Task> inFlight, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested && !Halted)
        {
            inFlight.RemoveAll(t => t.IsCompletedSuccessfully);
            try
            {
                await slots.WaitAsync(stopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            AttemptClaim? claim;
            try
            {
                await _claimGate.WaitAsync(stopping);
                try
                {
                    lock (_haltClaimGate)
                    {
                        // WaitAsync can grant a slot as halt cancels it. The
                        // claim and halt decision must share one fence.
                        if (stopping.IsCancellationRequested || Halted)
                        {
                            slots.Release();
                            return;
                        }

                        claim = store.BeginNextAttempt();
                    }
                }
                finally
                {
                    _claimGate.Release();
                }
            }
            catch (StorageException ex)
            {
                log($"dispatch claim failed: {ex.Failure}: {ex.Message}");
                if (ex.Failure != StorageFailure.Busy)
                {
                    slots.Release();
                    Halt("dispatcher_fault");
                    return;
                }
                claim = null;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                slots.Release();
                return;
            }
            catch (Exception ex)
            {
                log($"dispatch claim fault: {ex.GetType().Name}");
                slots.Release();
                Halt("dispatcher_fault");
                return;
            }

            if (claim is null)
            {
                slots.Release();
                try
                {
                    await _signal.WaitAsync(TimeSpan.FromSeconds(1), stopping);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            inFlight.Add(Task.Run(async () =>
            {
                try
                {
                    await RunAttemptAsync(claim, stopping);
                }
                catch
                {
                    Halt("dispatcher_fault");
                    throw;
                }
                finally
                {
                    slots.Release();

                    // A finished turn may unblock a follow-up waiting on its session.
                    _signal.Release();
                }
            }, CancellationToken.None));
        }
    }

    internal async Task RunAttemptAsync(AttemptClaim claim, CancellationToken daemonLifetime)
    {
        var run = new RunRef(claim.Job.JobId, claim.RunId, claim.Generation, claim.Correlation);
        using var stopRequested = new CancellationTokenSource();
        var active = new ActiveRun(stopRequested);
        _running[run.JobId] = active;
        IBackendRun? backendRun = null;
        try
        {
            // The deadline starts before any backend effect, so start and delivery are bounded too.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(daemonLifetime, stopRequested.Token);
            // Interactive launch and native transcript confirmation can exceed the fake
            // backend's short default runtime on a loaded desktop.
            var runtime = backends.Resolve(claim.Job.Backend) is HerdrInteractiveBackend or WtInteractiveBackend
                ? TimeSpan.FromTicks(Math.Max(limits.MaxFakeRuntime.Ticks,
                    (InteractiveStartup.Timeout * 2 + TimeSpan.FromSeconds(90)).Ticks))
                : limits.MaxFakeRuntime;
            deadline.CancelAfter(runtime);
            checkpoints.Hit(DurabilityCheckpoints.AttemptAfterCommit);

            // A job timeout ends the attempt through the same cancel path as a stop.
            using var jobTimeout = claim.Job.TimeoutSeconds is int seconds ? new CancellationTokenSource(TimeSpan.FromSeconds(seconds)) : null;
            using var onTimeout = jobTimeout?.Token.Register(() => CancelOwned(run.JobId, "timeout"));

            // A stop may have committed after claim but before this task was scheduled.
            if (store.GetJob(run.JobId)?.Status == JobStatus.Cancelled)
            {
                store.ReconcileStoppedJob(run.JobId); // No backend effect occurred.
                return;
            }

            deadline.Token.ThrowIfCancellationRequested();

            if (!TryPrepare(claim.Job, out var backend, out var resumeSessionId, out var notStarted))
            {
                // Nothing was started: no effect is possible.
                End(run, JobStatus.Failed, notStarted);
                return;
            }

            if (!JobWorktree.Prepare(claim.Job))
            {
                End(run, JobStatus.Failed, "worktree_unavailable");
                return;
            }

            var request = new BackendRequest(claim.Job.JobId, claim.Correlation, claim.Job.Instruction, claim.Job.Options)
            {
                StartupProgress = phase =>
                {
                    try { store.RecordStartup(run, phase); }
                    catch (StorageException) { /* Diagnostic only. */ }
                },
                ResumeSessionId = resumeSessionId,
                WorkingDirectory = JobWorktree.WorkingDirectory(claim.Job),
                Output = jobLogs?.BeginRun(claim.Job.JobId, claim.RunId, claim.Job.Backend),
            };
            request = childContext?.Prepare(request) ?? request;
            if (claim.Job.Backend == BackendCatalog.Pi && request.ManagedMcpConfig is not null && !PiMcpAdapter.IsInstalled(piHome))
            {
                End(run, JobStatus.Failed, "pi_mcp_adapter_missing", PiMcpAdapter.InstallHint);
                return;
            }
            var starting = Task.Run(() => backend.Start(request), CancellationToken.None);
            try
            {
                backendRun = await BackendCall(() => starting.WaitAsync(deadline.Token), "launch_failed");
            }
            catch (OperationCanceledException)
            {
                // Process start cannot be cancelled. A late start may exist: it is
                // terminated when it returns, never delivered to, never replaced.
                TerminateLateStart(starting, run, daemonLifetime.IsCancellationRequested && backend is HerdrInteractiveBackend);
                if (!daemonLifetime.IsCancellationRequested && !stopRequested.IsCancellationRequested)
                {
                    End(run, JobStatus.NeedsReconciliation, "backend_start_timeout");
                }

                return;
            }

            Volatile.Write(ref active.BackendRun, backendRun);
            deadline.Token.ThrowIfCancellationRequested();
            TryRecord(run, backendRun.ProcessId, acked: false);
            await BackendCall(async () =>
            {
                await backendRun.DeliverAsync(deadline.Token).WaitAsync(deadline.Token);
                return true;
            }, "backend_failed");
            var evidenceReader = await BackendCall(
                () => Task.FromResult(backendRun.ReadEvidenceAsync(deadline.Token).GetAsyncEnumerator(deadline.Token)), "backend_failed");
            try
            {
                while (await BackendCall(() => evidenceReader.MoveNextAsync().AsTask(), "backend_failed"))
                {
                    var evidence = evidenceReader.Current;
                    switch (evidence)
                    {
                        case BackendEvidence.Ack ack when ack.Correlation == claim.Correlation:
                            TryRecord(run, null, acked: true);
                            break;
                        case BackendEvidence.Session session when session.Correlation == claim.Correlation:
                            TryRecordSession(run, session.SessionId);
                            break;
                        case BackendEvidence.Result result when result.Correlation == claim.Correlation:
                            Complete(run, result.Output);
                            return;
                        case BackendEvidence.Ack or BackendEvidence.Result or BackendEvidence.Session:
                            log($"ignored stale/mismatched backend evidence for {run.RunId}");
                            break;
                        case BackendEvidence.ProtocolError error:
                            End(run, error.Code == JobErrors.SessionExpired ? JobStatus.Failed : JobStatus.NeedsReconciliation, error.Code);
                            return;
                        case BackendEvidence.NotStarted rejected:
                            End(run, JobStatus.Failed, "backend_not_started", rejected.Details);
                            return;
                        case BackendEvidence.LaunchFailed failed:
                            End(run, JobStatus.Failed, "launch_failed", failed.Details);
                            return;
                        case BackendEvidence.EndOfOutput:
                            End(run, JobStatus.NeedsReconciliation, "backend_eof");
                            return;
                    }
                }
            }
            finally
            {
                await BackendCall(async () =>
                {
                    await evidenceReader.DisposeAsync();
                    return true;
                }, "backend_failed");
            }

            End(run, JobStatus.NeedsReconciliation, "backend_eof");
        }
        catch (OperationCanceledException) when (stopRequested.IsCancellationRequested)
        {
            active.TerminateOnce(backend => TryTerminate(backend, run.JobId));
        }
        catch (OperationCanceledException) when (daemonLifetime.IsCancellationRequested)
        {
            // Interactive terminal sessions outlive the daemon; restart quarantines them.
            if (backends.Resolve(claim.Job.Backend) is not (HerdrInteractiveBackend or WtInteractiveBackend))
            {
                active.TerminateOnce(backend => TryTerminate(backend, run.JobId));
            }
        }
        catch (OperationCanceledException)
        {
            // Spike-only policy: kill our own direct child through its held handle.
            // A possible surviving child or failed kill stays uncertain, never retried.
            // Once-only, so a concurrent interrupt keeps its live interactive tab.
            active.TerminateOnce(backend => TryTerminate(backend, run.JobId));
            End(run, JobStatus.NeedsReconciliation, "backend_timeout");
        }
        catch (BackendCallException ex)
        {
            var blocked = ex.InnerException as AgentStartupBlockedException;
            var noEffects = blocked is not null || ex.InnerException is BackendNotStartedException && backendRun is null;
            var reason = blocked?.Reason ?? (noEffects ? "backend_not_started" : ex.Reason);
            var message = ex.InnerException!.Message;
            if (ex.InnerException.InnerException is not null && !message.Contains(ex.InnerException.GetBaseException().Message, StringComparison.Ordinal))
            {
                message += ": " + ex.InnerException.GetBaseException().Message;
            }
            log($"backend failure for {run.RunId}: {reason}: {message}");
            active.TerminateOnce(backend => TryTerminate(backend, run.JobId));
            End(run, noEffects ? JobStatus.Failed : JobStatus.NeedsReconciliation, reason,
                message);
            if (noEffects && store.GetJob(run.JobId)?.Status == JobStatus.Cancelled)
            {
                store.ReconcileStoppedJob(run.JobId);
            }
        }
        catch (Exception) when (stopRequested.IsCancellationRequested)
        {
            // Killing the owned child can surface as a stream or process error.
            active.TerminateOnce(backend => TryTerminate(backend, run.JobId));
        }
        catch (Exception ex)
        {
            // Unanticipated fault after the attempt commit: the effect is unknown, so
            // quarantine (never failed, never requeued) and stop claiming work.
            log($"dispatcher fault for {run.RunId}: {ex.GetType().Name}: {ex.Message}");
            active.TerminateOnce(backend => TryTerminate(backend, run.JobId));
            End(run, JobStatus.NeedsReconciliation, "dispatcher_fault");
            Halt("dispatcher_fault");
        }
        finally
        {
            // An interrupt kills before it cancels, so the turn can end first.
            if (stopRequested.IsCancellationRequested || active.Terminated)
            {
                // Descendants reparented away from the owned child escape a tree kill;
                // this run's unique marker still identifies them.
                OrphanedBackendProcess.TerminateMarked([run.Correlation]);
            }

            if (OperatingSystem.IsWindows() && backendRun is { OwnedChildAlive: true }
                && store.GetJob(run.JobId)?.Status == JobStatus.NeedsReconciliation)
            {
                // Windows cannot re-prove a terminal job's PID after its Process
                // handle is disposed. Retain that handle for an explicit stop.
                _reconciledWindows[run.JobId] = backendRun;
            }
            else { await DisposeQuietly(backendRun); }
            _running.TryRemove(run.JobId, out _);
            if (backendRun?.OwnedSessionStopped == true)
            {
                try { store.ReconcileStoppedJob(run.JobId); }
                catch (StorageException ex)
                {
                    log($"session reconciliation failed for {run.JobId}: {ex.Message}");
                    Halt("terminal_write_failed");
                }
            }
        }
    }

    // Only exceptions crossing a backend boundary are job-scoped. Store and
    // dispatcher failures outside these calls retain the daemon-wide halt path.
    static async Task<T> BackendCall<T>(Func<Task<T>> call, string reason)
    {
        try { return await call(); }
        catch (Exception ex) when (ex is not (OperationCanceledException or StorageException or InjectedFailureException))
        {
            throw new BackendCallException(reason, ex);
        }
    }

    sealed class BackendCallException(string reason, Exception inner) : Exception(inner.Message, inner)
    {
        public string Reason { get; } = reason;
    }

    /// <summary>Resolves the job's backend and, for a follow-up, the parent's native session.</summary>
    bool TryPrepare(JobRecord job, [NotNullWhen(true)] out IJobBackend? backend, out string? resumeSessionId, out string notStarted)
    {
        resumeSessionId = null;
        notStarted = "backend_unavailable";
        backend = backends.Resolve(job.Backend);
        if (backend is null || job.ParentJobId is null)
        {
            return backend is not null;
        }

        resumeSessionId = store.GetJob(job.ParentJobId)?.SessionId;

        notStarted = "parent_session_missing";
        return resumeSessionId is not null;
    }

    void TerminateLateStart(Task<IBackendRun> starting, RunRef run, bool preserve) =>
        _ = starting.ContinueWith(async late =>
        {
            _ = late.Exception;
            if (late.IsCompletedSuccessfully)
            {
                log($"late backend start for {run.RunId}; preserve interactive session: {preserve}");
                if (!preserve) { TryTerminate(late.Result, run.JobId); }
                await DisposeQuietly(late.Result);
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();

    void TryTerminate(IBackendRun? backendRun, string jobId)
    {
        try
        {
            backendRun?.TerminateOwnedChild();
            if (backendRun is not null && store.GetJob(jobId)?.Status == JobStatus.Cancelled)
            {
                store.ReconcileStoppedJob(jobId);
            }
        }
        catch (Exception ex)
        {
            var details = ex is AggregateException aggregate
                ? string.Join("; ", aggregate.Flatten().InnerExceptions.Select(inner => $"{inner.GetType().Name}: {inner.Message}"))
                : $"{ex.GetType().Name}: {ex.Message}";
            log($"owned child termination failed: {details}");
        }
    }

    void TryInterrupt(IBackendRun? backendRun, string jobId)
    {
        try
        {
            backendRun?.InterruptTurn();
            if (backendRun is not null) { store.ReconcileStoppedJob(jobId); }
        }
        catch (Exception ex)
        {
            log($"owned turn interruption failed: {ex.GetType().Name}; stopping owned backend");
            TryTerminate(backendRun, jobId);
        }
    }

    sealed class ActiveRun(CancellationTokenSource stop)
    {
        int _terminated;

        public CancellationTokenSource Stop { get; } = stop;

        public IBackendRun? BackendRun;

        public bool Terminated => Volatile.Read(ref _terminated) != 0;

        public bool TerminateOnce(Action<IBackendRun?> terminate)
        {
            var backendRun = Volatile.Read(ref BackendRun);
            if (backendRun is null || Interlocked.Exchange(ref _terminated, 1) != 0) { return false; }
            terminate(backendRun);
            return true;
        }
    }

    async Task DisposeQuietly(IBackendRun? backendRun)
    {
        if (backendRun is null)
        {
            return;
        }

        try
        {
            await backendRun.DisposeAsync();
        }
        catch (Exception ex)
        {
            log($"backend dispose failed: {ex.GetType().Name}");
        }
    }

    void Halt(string reason)
    {
        lock (_haltClaimGate)
        {
            Interlocked.CompareExchange(ref _haltReason, reason, null);
            admission.Close(HaltReason!);
        }

        _halted.Cancel();
    }

    void Complete(RunRef run, string output)
    {
        try
        {
            if (!store.Complete(run, output))
            {
                log($"completion fenced out for {run.RunId}");
            }
        }
        catch (Exception ex) when (ex is StorageException or InjectedFailureException)
        {
            log($"completion write failed for {run.RunId}; marking uncertain");
            End(run, JobStatus.NeedsReconciliation, "completion_write_failed");
            Halt("terminal_write_failed");
        }
    }

    void End(RunRef run, string status, string reason, string? message = null)
    {
        try
        {
            store.EndUnsuccessfully(run, status, reason, message);
        }
        catch (Exception ex)
        {
            log($"terminal write failed for {run.RunId}: {(ex is StorageException storage ? storage.Failure.ToString() : ex.GetType().Name)}; halting dispatch");
            Halt("terminal_write_failed");
        }
    }

    void TryRecordSession(RunRef run, string sessionId)
    {
        try
        {
            store.RecordSession(run, sessionId);
        }
        catch (StorageException ex)
        {
            // Without it a follow-up is refused (parent_not_ready); the turn itself is unaffected.
            log($"session record failed for {run.RunId}: {ex.Failure}");
        }
    }

    void TryRecord(RunRef run, int? pid, bool acked)
    {
        try
        {
            store.RecordBackendEvidence(run, pid, acked);
        }
        catch (StorageException)
        {
            // Diagnostic evidence only; state is unaffected.
        }
    }
}

/// <summary>Raised by a test-profile checkpoint to simulate a failed durable write.</summary>
public sealed class InjectedFailureException(string checkpoint) : Exception($"injected failure at {checkpoint}");
