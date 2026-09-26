namespace AgentTeamForge.Tests.Support;

/// <summary>
/// MODEL ONLY. A small deterministic model of the public Codex 0.157.1 thread
/// queue contract as read from source (openai/codex 36650394c5b3, see
/// docs/research/codex-queue-admission.md). It is not a Codex client and proves
/// nothing about native behaviour; it exists to express protocol counterexamples
/// that the application must fence. This is the durable queue store (shared by
/// every runtime); <see cref="Runtime"/> is one server process loading a thread.
/// </summary>
public sealed class CodexQueueModel
{
    public enum Origin
    {
        Human,
        Queue,
    }

    public enum TurnEnd
    {
        Completed,
        Failed,
        Interrupted,
    }

    public enum StartOutcome
    {
        Started,
        Busy,
        NotFound,
    }

    public sealed record QueueRow(string QueueId, string ClientMessageId, string Text);

    /// <summary>Returned by add: queue identity and caller correlation, never turn completion.</summary>
    public sealed record AddReply(string QueueId, string ClientMessageId);

    /// <summary>One input presented to a turn; Execution is the append-only history.</summary>
    public sealed record Presented(string Runtime, int TurnId, Origin TurnOrigin, Origin InputOrigin, string? QueueId, string? ClientMessageId, string Text);

    public sealed class Turn(int id, Origin origin, string runtime)
    {
        public int Id { get; } = id;

        public Origin Origin { get; } = origin;

        public string Runtime { get; } = runtime;

        public List<string> Inputs { get; } = [];
    }

    /// <summary>Thrown by a runtime configured to die after start and before row deletion.</summary>
    public sealed class SimulatedCrashException() : Exception("runtime crashed after start, before queue row deletion");

    readonly Lock _store = new();
    readonly List<QueueRow> _rows = [];
    readonly List<Presented> _history = [];
    int _nextQueueId;
    int _nextTurnId;

    public IReadOnlyList<QueueRow> Rows
    {
        get
        {
            lock (_store)
            {
                return [.. _rows];
            }
        }
    }

    public IReadOnlyList<Presented> History
    {
        get
        {
            lock (_store)
            {
                return [.. _history];
            }
        }
    }

    public int Executions(string clientMessageId) =>
        History.Count(p => p.InputOrigin == Origin.Queue && p.ClientMessageId == clientMessageId);

    /// <summary>A server process loading the thread from this store (initial load or restart).</summary>
    public Runtime Load(string name) => new(this, name);

    /// <summary>
    /// Application requirement, not a native capability: once a human input has
    /// been presented, no queued input may be presented until the application
    /// explicitly reconciles, and no human input may join a queue-started turn.
    /// The queue contract has no reconciliation hold, so this only checks a trace.
    /// </summary>
    public bool StrictHumanPauseViolated()
    {
        var humanSeen = false;
        foreach (var p in History)
        {
            if (p.InputOrigin == Origin.Human)
            {
                if (p.TurnOrigin == Origin.Queue)
                {
                    return true;
                }

                humanSeen = true;
            }
            else if (humanSeen)
            {
                return true;
            }
        }

        return false;
    }

    QueueRow Insert(string clientMessageId, string text)
    {
        lock (_store)
        {
            var row = new QueueRow($"q{++_nextQueueId}", clientMessageId, text);
            _rows.Add(row);
            return row;
        }
    }

    QueueRow? First()
    {
        lock (_store)
        {
            return _rows.Count == 0 ? null : _rows[0];
        }
    }

    bool Contains(string queueId)
    {
        lock (_store)
        {
            return _rows.Exists(r => r.QueueId == queueId);
        }
    }

    bool Remove(string queueId)
    {
        lock (_store)
        {
            return _rows.RemoveAll(r => r.QueueId == queueId) == 1;
        }
    }

    Turn NewTurn(Origin origin, string runtime)
    {
        lock (_store)
        {
            return new Turn(++_nextTurnId, origin, runtime);
        }
    }

    void Present(Turn turn, Origin inputOrigin, string? queueId, string? clientMessageId, string text)
    {
        lock (_store)
        {
            turn.Inputs.Add(text);
            _history.Add(new Presented(turn.Runtime, turn.Id, turn.Origin, inputOrigin, queueId, clientMessageId, text));
        }
    }

    /// <summary>
    /// One process loading the thread. Its dispatch lock and active turn are
    /// process-local: nothing here coordinates with another runtime on the same store.
    /// </summary>
    public sealed class Runtime
    {
        readonly Lock _dispatch = new();
        readonly CodexQueueModel _store;
        Turn? _active;
        bool _interrupted;
        bool _dead;

        internal Runtime(CodexQueueModel store, string name)
        {
            _store = store;
            Name = name;
        }

        public string Name { get; }

        public Turn? Active
        {
            get
            {
                lock (_dispatch)
                {
                    return _active;
                }
            }
        }

        /// <summary>Fault injection: die after a queued turn starts, before its row is deleted.</summary>
        public bool CrashAfterStartBeforeDelete { get; set; }

        /// <summary>
        /// thread/queue/add: busy does not reject. Persists a row with a fresh
        /// server queue ID, then wakes dispatch before replying, so the input can
        /// already be running when the reply arrives. A dropped reply loses only
        /// the reply, never the row.
        /// </summary>
        public AddReply? Add(string clientMessageId, string text, bool dropReply = false)
        {
            lock (_dispatch)
            {
                EnsureAlive();
                var row = _store.Insert(clientMessageId, text);
                Drain();
                return dropReply ? null : new AddReply(row.QueueId, row.ClientMessageId);
            }
        }

        /// <summary>An add that is still in transit; invoking the result makes it arrive.</summary>
        public Func<AddReply?> SendDelayed(string clientMessageId, string text) => () => Add(clientMessageId, text);

        /// <summary>Ordinary turn start is start-or-steer: a human input joins any active turn.</summary>
        public Turn HumanStart(string text)
        {
            lock (_dispatch)
            {
                EnsureAlive();
                _active ??= _store.NewTurn(Origin.Human, Name);
                _interrupted = false;
                _store.Present(_active, Origin.Human, null, null, text);
                return _active;
            }
        }

        /// <summary>
        /// Completed and failed lifecycles drain the next row. Interrupted does not,
        /// and the retained interrupted status keeps later adds and change wakes
        /// from draining until a new turn starts or the thread is really resumed.
        /// </summary>
        public void EndTurn(TurnEnd end)
        {
            lock (_dispatch)
            {
                EnsureAlive();
                _active = null;
                _interrupted = end == TurnEnd.Interrupted;
                Drain();
            }
        }

        /// <summary>thread/queue/start: never steers; busy keeps the row.</summary>
        public StartOutcome ExplicitStart(string queueId)
        {
            lock (_dispatch)
            {
                EnsureAlive();
                if (!_store.Contains(queueId))
                {
                    return StartOutcome.NotFound;
                }

                if (_active is not null)
                {
                    return StartOutcome.Busy;
                }

                var row = _store.Rows.First(r => r.QueueId == queueId);
                StartThenDelete(row);
                return StartOutcome.Started;
            }
        }

        /// <summary>Returns whether a row was removed; says nothing about earlier execution.</summary>
        public bool Delete(string queueId)
        {
            lock (_dispatch)
            {
                EnsureAlive();
                return _store.Remove(queueId);
            }
        }

        /// <summary>Change wake or external-change retry: dispatch persisted input if idle and not interrupted.</summary>
        public void Wake()
        {
            lock (_dispatch)
            {
                EnsureAlive();
                Drain();
            }
        }

        /// <summary>Real thread resume: clears interrupted status, then dispatches persisted input if idle.</summary>
        public void Resume()
        {
            lock (_dispatch)
            {
                EnsureAlive();
                _interrupted = false;
                Drain();
            }
        }

        /// <summary>Dispatch step 1 (read), exposed to interleave two runtimes deterministically.</summary>
        public QueueRow? Peek()
        {
            lock (_dispatch)
            {
                EnsureAlive();
                return _store.First();
            }
        }

        /// <summary>Dispatch step 2 (start_turn_if_idle), without step 3 (row delete).</summary>
        public StartOutcome StartIfIdle(QueueRow row)
        {
            lock (_dispatch)
            {
                EnsureAlive();
                if (_active is not null)
                {
                    return StartOutcome.Busy;
                }

                Start(row);
                return StartOutcome.Started;
            }
        }

        void Drain()
        {
            if (_active is null && !_interrupted && _store.First() is { } row)
            {
                StartThenDelete(row);
            }
        }

        void StartThenDelete(QueueRow row)
        {
            Start(row);
            if (CrashAfterStartBeforeDelete)
            {
                _dead = true;
                throw new SimulatedCrashException();
            }

            _store.Remove(row.QueueId);
        }

        void Start(QueueRow row)
        {
            _interrupted = false;
            _active = _store.NewTurn(Origin.Queue, Name);
            _store.Present(_active, Origin.Queue, row.QueueId, row.ClientMessageId, row.Text);
        }

        void EnsureAlive() => ObjectDisposedException.ThrowIf(_dead, this);
    }
}
