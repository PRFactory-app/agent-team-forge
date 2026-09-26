using System.Diagnostics.CodeAnalysis;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>
/// Single dispatcher: claims one unattempted intent at a time, commits the
/// attempt-start before any backend effect, and turns backend evidence into
/// exactly one fenced terminal write. Driven by the daemon lifetime token only.
/// One deadline bounds the whole effect: start, delivery and evidence reading.
/// </summary>
public sealed class DispatchJob : IDisposable
{
    /// <summary>Upper bound for a configured maximum runtime.</summary>
    public static readonly TimeSpan MaxAllowedRuntime = TimeSpan.FromHours(24);

    readonly SemaphoreSlim _signal = new(0);
    readonly SemaphoreSlim _claimGate = new(1, 1);
    readonly JobStore store;
    readonly BackendCatalog backends;
    readonly SpikeLimits limits;
    readonly DurabilityCheckpoints checkpoints;
    readonly AdmissionGate admission;
    readonly Action<string> log;

    /// <summary>Single-backend convenience: serves jobs whose backend is "fake".</summary>
    public DispatchJob(JobStore store, IJobBackend backend, SpikeLimits limits, DurabilityCheckpoints checkpoints, AdmissionGate admission, Action<string> log)
        : this(store, new BackendCatalog().Register(BackendCatalog.Fake, () => backend), limits, checkpoints, admission, log)
    {
    }

    public DispatchJob(JobStore store, BackendCatalog backends, SpikeLimits limits, DurabilityCheckpoints checkpoints, AdmissionGate admission, Action<string> log)
    {
        // Validated before the daemon reports readiness; CancelAfter would otherwise fault the loop.
        if (limits.MaxFakeRuntime <= TimeSpan.Zero || limits.MaxFakeRuntime > MaxAllowedRuntime)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), limits.MaxFakeRuntime, "max runtime must be positive and bounded");
        }

        this.store = store;
        this.backends = backends;
        this.limits = limits;
        this.checkpoints = checkpoints;
        this.admission = admission;
        this.log = log;
    }

    /// <summary>
    /// Why the dispatcher stopped claiming work (terminal_write_failed or
    /// dispatcher_fault); null while healthy. Setting it closes admission at the
    /// same instant; <see cref="RunAsync"/> then returns and the host exits
    /// unhealthy. Restart quarantines the run.
    /// </summary>
    public string? HaltReason { get; private set; }

    public bool Halted => HaltReason is not null;

    public void Signal() => _signal.Release();

    /// <summary>Keep a new submission out of the claim loop until its reply is sent.</summary>
    public IDisposable PauseClaims()
    {
        _claimGate.Wait();
        return new ClaimPause(_claimGate);
    }

    public void Dispose()
    {
        _signal.Dispose();
        _claimGate.Dispose();
    }

    sealed class ClaimPause(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    /// <summary>Admission is closed whenever this returns or throws, halted or shut down.</summary>
    public async Task RunAsync(CancellationToken daemonLifetime)
    {
        try
        {
            await ClaimLoopAsync(daemonLifetime);
        }
        catch
        {
            Halt("dispatcher_fault");
            throw;
        }
        finally
        {
            admission.Close(HaltReason ?? "daemon_stopping");
        }
    }

    async Task ClaimLoopAsync(CancellationToken daemonLifetime)
    {
        while (!daemonLifetime.IsCancellationRequested && !Halted)
        {
            AttemptClaim? claim;
            try
            {
                await _claimGate.WaitAsync(daemonLifetime);
                try
                {
                    claim = store.BeginNextAttempt();
                }
                finally
                {
                    _claimGate.Release();
                }
            }
            catch (StorageException ex)
            {
                log($"dispatch claim failed: {ex.Failure}");
                claim = null;
            }
            catch (OperationCanceledException) when (daemonLifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log($"dispatch claim fault: {ex.GetType().Name}");
                Halt("dispatcher_fault");
                return;
            }

            if (claim is null)
            {
                try
                {
                    await _signal.WaitAsync(TimeSpan.FromSeconds(1), daemonLifetime);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            await RunAttemptAsync(claim, daemonLifetime);
        }
    }

    internal async Task RunAttemptAsync(AttemptClaim claim, CancellationToken daemonLifetime)
    {
        var run = new RunRef(claim.Job.JobId, claim.RunId, claim.Generation, claim.Correlation);
        IBackendRun? backendRun = null;
        try
        {
            // The deadline starts before any backend effect, so start and delivery are bounded too.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(daemonLifetime);
            deadline.CancelAfter(limits.MaxFakeRuntime);
            checkpoints.Hit(DurabilityCheckpoints.AttemptAfterCommit);

            if (!TryPrepare(claim.Job, out var backend, out var resumeSessionId, out var notStarted))
            {
                // Nothing was started: no effect is possible.
                End(run, JobStatus.Failed, notStarted);
                return;
            }

            var request = new BackendRequest(claim.Job.JobId, claim.Correlation, claim.Job.Instruction, claim.Job.Options)
            {
                ResumeSessionId = resumeSessionId,
                WorkingDirectory = claim.Job.Cwd,
            };
            var starting = Task.Run(() => backend.Start(request), CancellationToken.None);
            try
            {
                backendRun = await starting.WaitAsync(deadline.Token);
            }
            catch (BackendNotStartedException)
            {
                // Proven no effect: the only case allowed to end as failed.
                End(run, JobStatus.Failed, "backend_not_started");
                return;
            }
            catch (OperationCanceledException)
            {
                // Process start cannot be cancelled. A late start may exist: it is
                // terminated when it returns, never delivered to, never replaced.
                TerminateLateStart(starting, run);
                if (!daemonLifetime.IsCancellationRequested)
                {
                    End(run, JobStatus.NeedsReconciliation, "backend_start_timeout");
                }

                return;
            }

            TryRecord(run, backendRun.ProcessId, acked: false);
            await backendRun.DeliverAsync(deadline.Token).WaitAsync(deadline.Token);
            await foreach (var evidence in backendRun.ReadEvidenceAsync(deadline.Token))
            {
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
                        End(run, JobStatus.NeedsReconciliation, error.Code);
                        return;
                    case BackendEvidence.EndOfOutput:
                        End(run, JobStatus.NeedsReconciliation, "backend_eof");
                        return;
                }
            }

            End(run, JobStatus.NeedsReconciliation, "backend_eof");
        }
        catch (OperationCanceledException) when (daemonLifetime.IsCancellationRequested)
        {
            // Daemon shutdown: leave the attempt started; restart recovery quarantines it.
        }
        catch (OperationCanceledException)
        {
            // Spike-only policy: kill our own direct child through its held handle.
            // A possible surviving child or failed kill stays uncertain, never retried.
            TryTerminate(backendRun);
            End(run, JobStatus.NeedsReconciliation, "backend_timeout");
        }
        catch (Exception ex)
        {
            // Unanticipated fault after the attempt commit: the effect is unknown, so
            // quarantine (never failed, never requeued) and stop claiming work.
            log($"dispatcher fault for {run.RunId}: {ex.GetType().Name}");
            TryTerminate(backendRun);
            End(run, JobStatus.NeedsReconciliation, "dispatcher_fault");
            Halt("dispatcher_fault");
        }
        finally
        {
            await DisposeQuietly(backendRun);
        }
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

        try
        {
            resumeSessionId = store.GetJob(job.ParentJobId)?.SessionId;
        }
        catch (StorageException)
        {
            resumeSessionId = null;
        }

        notStarted = "parent_session_missing";
        return resumeSessionId is not null;
    }

    void TerminateLateStart(Task<IBackendRun> starting, RunRef run) =>
        _ = starting.ContinueWith(async late =>
        {
            _ = late.Exception;
            if (late.IsCompletedSuccessfully)
            {
                log($"late backend start for {run.RunId}; terminating owned child");
                TryTerminate(late.Result);
                await DisposeQuietly(late.Result);
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();

    void TryTerminate(IBackendRun? backendRun)
    {
        try
        {
            backendRun?.TerminateOwnedChild();
        }
        catch (Exception ex)
        {
            log($"owned child termination failed: {ex.GetType().Name}");
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
        HaltReason ??= reason;
        admission.Close(HaltReason);
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

    void End(RunRef run, string status, string reason)
    {
        try
        {
            store.EndUnsuccessfully(run, status, reason);
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
