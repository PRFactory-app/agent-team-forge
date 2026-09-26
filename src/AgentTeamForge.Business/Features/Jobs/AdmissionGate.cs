namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>
/// The one admission fence shared by the dispatcher and submission. Closing
/// and entering are linearized under one lock: a submission that entered
/// before <see cref="Close"/> may finish its durable transaction (the job stays
/// queued for restart); one that begins after it is refused without a write.
/// Closing is permanent for this daemon process.
/// </summary>
public sealed class AdmissionGate
{
    readonly Lock _sync = new();
    readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _inFlight;
    string? _closedReason;

    public string? ClosedReason
    {
        get
        {
            lock (_sync)
            {
                return _closedReason;
            }
        }
    }

    /// <summary>Completes once the gate is closed and every admitted submission has exited.</summary>
    public Task Drained => _drained.Task;

    public bool TryEnter()
    {
        lock (_sync)
        {
            if (_closedReason is not null)
            {
                return false;
            }

            _inFlight++;
            return true;
        }
    }

    public void Exit()
    {
        lock (_sync)
        {
            _inFlight--;
            if (_closedReason is not null && _inFlight == 0)
            {
                _drained.TrySetResult();
            }
        }
    }

    /// <summary>Idempotent; the first reason is kept.</summary>
    public void Close(string reason)
    {
        lock (_sync)
        {
            _closedReason ??= reason;
            if (_inFlight == 0)
            {
                _drained.TrySetResult();
            }
        }
    }
}
