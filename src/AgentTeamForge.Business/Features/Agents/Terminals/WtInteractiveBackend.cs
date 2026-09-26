using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>Runs an interactive agent in an owned Windows Terminal tab.</summary>
public sealed class WtInteractiveBackend : IJobBackend
{
    public static int RecoverOwned(string stateRoot) => WtTabControl.RecoverOwned(stateRoot);
    readonly IWtTabControl _tabs;
    readonly IInteractiveTranscriptReader _transcripts;
    readonly InteractiveAgentKind _kind;
    readonly string _stateRoot;
    readonly string _tabDirectory;

    public WtInteractiveBackend(InteractiveAgentKind kind, string stateRoot)
        : this(new WtTabControl(), new InteractiveTranscriptReader(), kind, stateRoot, "wt") { }

    internal WtInteractiveBackend(IWtTabControl tabs, IInteractiveTranscriptReader transcripts, InteractiveAgentKind kind, string stateRoot)
        : this(tabs, transcripts, kind, stateRoot, "wt") { }

    internal WtInteractiveBackend(IWtTabControl tabs, IInteractiveTranscriptReader transcripts, InteractiveAgentKind kind, string stateRoot, string tabDirectory)
    {
        _tabs = tabs;
        _transcripts = transcripts;
        _kind = kind;
        _stateRoot = stateRoot;
        _tabDirectory = tabDirectory;
    }

    public IBackendRun Start(BackendRequest request)
    {
        var cwd = request.WorkingDirectory ?? Environment.CurrentDirectory;
        if (!Path.IsPathFullyQualified(cwd) || !Directory.Exists(cwd))
        {
            throw new BackendNotStartedException("interactive working directory does not exist");
        }

        _tabs.Preflight(_kind);
        var agentName = "atf" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10));
        var piDirectory = _kind == InteractiveAgentKind.Pi ? PiDirectory(request) : null;
        var launch = new InteractiveLaunch(_kind, agentName, cwd, request.ResumeSessionId, piDirectory,
            Path.Combine(_stateRoot, _tabDirectory, agentName + (_tabDirectory == "wt" ? ".launch.ps1" : ".launch.sh")));
        if (OperatingSystem.IsWindows())
        {
            // Reject unsafe .cmd shim arguments or hook paths here, where not-started is provable.
            _ = WtTabControl.AgentArguments(launch, "");
        }
        return new Run(_tabs, _transcripts, request, launch, DateTimeOffset.UtcNow);
    }

    string PiDirectory(BackendRequest request)
    {
        var root = Path.Combine(_stateRoot, "pi-sessions");
        if (request.ResumeSessionId is { } id)
        {
            return _transcripts.FindPiSessionDirectory(root, id)
                ?? throw new BackendNotStartedException("Pi session to resume was not found");
        }
        return Path.Combine(root, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)));
    }

    sealed class Run(IWtTabControl tabs, IInteractiveTranscriptReader transcripts, BackendRequest request,
        InteractiveLaunch launch, DateTimeOffset started) : IBackendRun
    {
        bool _launched;
        bool _completed;
        int _loggedMessages;
        public int? ProcessId => tabs.ProcessId(launch);

        public async Task DeliverAsync(CancellationToken cancellationToken)
        {
            var marker = "atf-corr:" + request.Correlation;
            var prompt = request.Instruction + "\n\n[AgentTeamForge correlation id: " + marker + " — internal marker, ignore this line]";
            try
            {
                await tabs.StartAsync(launch, prompt, cancellationToken);
                _launched = true;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                // wt.exe can hand the tab to an existing window before returning an error.
                // A failed launch is uncertain, so the dispatcher must reconcile it.
            }
        }

        public async IAsyncEnumerable<BackendEvidence> ReadEvidenceAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (!_launched)
            {
                yield return new BackendEvidence.ProtocolError("interactive_delivery_not_confirmed");
                yield break;
            }
            yield return new BackendEvidence.Ack(request.Correlation);
            var session = request.ResumeSessionId;
            if (session is not null)
            {
                yield return new BackendEvidence.Session(request.Correlation, session);
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var output = transcripts.Read(launch, "atf-corr:" + request.Correlation, started);
                if (output is not null && request.Output is { } log)
                {
                    for (var i = _loggedMessages; i < output.Progress.Count; i++)
                    {
                        log("transcript", Encoding.UTF8.GetBytes(output.Progress[i] + "\n"));
                    }
                    _loggedMessages = output.Progress.Count;
                }
                if (output?.SessionId is { } nativeId && nativeId != session)
                {
                    session = nativeId;
                    yield return new BackendEvidence.Session(request.Correlation, nativeId);
                }
                if (output?.Completed == true && output.Message is { Length: > 0 } message && session is not null)
                {
                    _completed = true;
                    yield return new BackendEvidence.Result(request.Correlation, message);
                    yield return new BackendEvidence.EndOfOutput();
                    yield break;
                }
                if (!tabs.IsAlive(launch))
                {
                    _completed = true;
                    yield return new BackendEvidence.ProtocolError("interactive_agent_exited");
                    yield break;
                }
                await Task.Delay(250, cancellationToken);
            }
        }

        public void TerminateOwnedChild() => tabs.StopOwned(launch);

        public ValueTask DisposeAsync()
        {
            if (_completed)
            {
                tabs.StopOwned(launch);
            }
            return ValueTask.CompletedTask;
        }
    }
}

internal interface IWtTabControl
{
    void Preflight(InteractiveAgentKind kind);
    Task StartAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken);
    bool IsAlive(InteractiveLaunch launch);
    int? ProcessId(InteractiveLaunch launch);
    void StopOwned(InteractiveLaunch launch);
}
