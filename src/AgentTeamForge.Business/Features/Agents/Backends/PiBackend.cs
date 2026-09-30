using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentTeamForge.Business.Features.Jobs;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>
/// Headless Pi (<c>pi -p --mode json</c>). A new run pins its session with
/// <c>--session-id</c>; a follow-up resumes it with <c>--session</c>. The
/// instruction travels on stdin (verbatim; no argv length or leading-character
/// dispatch issues). Options: <c>model=...;thinking=...</c>.
/// </summary>
public sealed class PiBackend(string executable = "pi") : IJobBackend
{
    public const int MaxLineBytes = 8 * 1024 * 1024;
    public const int MaxResultChars = 1_000_000;

    // Human-question tools would block a headless run forever (see reference).
    const string ExcludedTools = "ask_user,ask_question,ask_human,request_input";

    public IBackendRun Start(BackendRequest request)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (request.WorkingDirectory is { } cwd)
        {
            info.WorkingDirectory = cwd;
        }
        ManagedChildContext.ClearInheritedIdentity(info);
        if (request.ManagedMcpConfig is not null) { info.Environment["PI_MCP_CONFIG_MODE"] = "exclusive"; }
        OrphanedBackendProcess.Mark(info, request.Correlation);

        foreach (var argument in BuildArguments(request))
        {
            info.ArgumentList.Add(argument);
        }
        WindowsCliLaunch.Configure(info, "pi", executable == "pi");

        Process process;
        try
        {
            AgentTeamForge.Business.Features.Processes.NonInteractiveProcess.OwnProcessGroup(info);
            process = Process.Start(info) ?? throw new BackendNotStartedException("pi did not start");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            throw new BackendNotStartedException("pi could not be started", ex);
        }

        return new PiRun(process, request.Correlation, Encoding.UTF8.GetBytes(request.Instruction), request.Output, request.ResumeSessionId is not null);
    }

    internal static List<string> BuildArguments(BackendRequest request)
    {
        List<string> args = ["-p", "--mode", "json", "--approve", "--exclude-tools", ExcludedTools];
        args.AddRange(ManagedChildContext.Arguments("pi", request.ManagedMcpConfig));
        if (request.ResumeSessionId is { } session)
        {
            args.AddRange(["--session", session]);
        }
        else
        {
            args.AddRange(["--session-id", Guid.NewGuid().ToString()]);
        }

        foreach (var part in request.Options.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2);
            if (pair is ["model", { Length: > 0 } model])
            {
                args.AddRange(["--model", model.Contains('/') ? model : "openai-codex/" + model]);
            }
            else if (pair is ["thinking", { Length: > 0 } thinking] && PiThinking.Valid(thinking))
            {
                args.AddRange(["--thinking", thinking]);
            }
            else if (pair is ["effort", { Length: > 0 } effort] && PiThinking.Valid(effort))
            {
                args.AddRange(["--thinking", effort]);
            }
        }

        return args;
    }

    sealed class PiRun(Process process, string correlation, byte[] instruction, Action<string, ReadOnlyMemory<byte>>? output, bool resuming) : IBackendRun
    {
        readonly Process _process = process;
        readonly Stream _stdout = new CapturingReadStream(process.StandardOutput.BaseStream, "stdout", output);
        readonly Task<string> _stderrDrain = BackendSessionErrors.ReadStderrAsync(new CapturingReadStream(process.StandardError.BaseStream, "stderr", output));
        bool _deliveryFailed;

        public int? ProcessId => _process.Id;
        public bool OwnedChildAlive => !_process.HasExited;

        public async Task DeliverAsync(CancellationToken cancellationToken)
        {
            try
            {
                var stdin = _process.StandardInput.BaseStream;
                await stdin.WriteAsync(instruction, cancellationToken);
                await stdin.FlushAsync(cancellationToken);
                _process.StandardInput.Close();
            }
            catch (IOException)
            {
                _deliveryFailed = true;
            }
        }

        public async IAsyncEnumerable<BackendEvidence> ReadEvidenceAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (_deliveryFailed)
            {
                if (await UnknownOptionAsync(cancellationToken) is { } unknownOption)
                {
                    yield return new BackendEvidence.NotStarted(unknownOption);
                    yield break;
                }
                yield return new BackendEvidence.ProtocolError(resuming && await BackendSessionErrors.HasExpiredDiagnosticAsync(_stderrDrain, cancellationToken)
                    ? "session_expired" : "backend_delivery_failed");
                yield break;
            }

            var turn = new TurnState();
            await foreach (var line in ReadLinesAsync(_stdout, cancellationToken,
                () =>
                {
                    turn.MarkSkippedLine();
                    output?.Invoke("status", "[stdout line omitted: too large]\n"u8.ToArray());
                }))
            {
                foreach (var evidence in turn.Observe(line, correlation))
                {
                    yield return evidence;
                }
            }

            if (resuming && await BackendSessionErrors.HasExpiredDiagnosticAsync(_stderrDrain, cancellationToken))
            {
                yield return new BackendEvidence.ProtocolError("session_expired");
                yield break;
            }

            if (!turn.Started && await UnknownOptionAsync(cancellationToken) is { } diagnostic)
            {
                yield return new BackendEvidence.NotStarted(diagnostic);
                yield break;
            }

            if (turn.FinishAtEndOfOutput() is { } final)
            {
                yield return final;
            }

            yield return new BackendEvidence.EndOfOutput();
        }

        async Task<string?> UnknownOptionAsync(CancellationToken cancellationToken)
        {
            try
            {
                var diagnostic = await _stderrDrain.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
                if (!diagnostic.Contains("Unknown option", StringComparison.OrdinalIgnoreCase)) { return null; }
                await _process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
                return diagnostic.Trim();
            }
            catch (TimeoutException) { return null; }
        }

        public void TerminateOwnedChild() => OwnedProcessTermination.Kill(_process);

        public async ValueTask DisposeAsync()
        {
            var stdoutDrain = DrainAsync(_stdout);
            try
            {
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                // A still-running child stays recorded as uncertain; it is not adopted or killed here.
            }

            try
            {
                await _stderrDrain.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
            }

            try
            {
                await stdoutDrain.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
            }

            _process.Dispose();
        }
    }

    /// <summary>Maps Pi JSON events to evidence; the final answer is the last assistant message.</summary>
    internal sealed class TurnState
    {
        bool _finished;
        string? _text;
        string? _stopReason;
        string? _errorMessage;
        bool _skippedLine;

        public bool Started { get; private set; }

        public void MarkSkippedLine() => _skippedLine = true;

        public IEnumerable<BackendEvidence> Observe(ReadOnlyMemory<byte> line, string correlation)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // Pi's stdout is JSON lines; stray noise is not evidence.
                yield break;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type))
                {
                    yield break;
                }

                switch (type.GetString())
                {
                    case "session" when root.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } sessionId:
                        yield return new BackendEvidence.Session(correlation, sessionId);
                        break;
                    case "agent_start" when !Started:
                        Started = true;
                        yield return new BackendEvidence.Ack(correlation);
                        break;
                    case "message_end" when root.TryGetProperty("message", out var message) && IsAssistant(message):
                        _text = TextOf(message);
                        _stopReason = message.TryGetProperty("stopReason", out var stop) ? stop.GetString() : null;
                        _errorMessage = message.TryGetProperty("errorMessage", out var error) && error.ValueKind == JsonValueKind.String
                            ? error.GetString() : null;
                        break;
                    case "agent_settled" when Finish(correlation) is { } final:
                        yield return final;
                        break;
                }
            }
        }

        public BackendEvidence? Finish(string correlation)
        {
            if (_finished)
            {
                return null;
            }

            _finished = true;
            return (_text, _stopReason) switch
            {
                (null, _) when _skippedLine => new BackendEvidence.ProtocolError("backend_malformed_output"),
                (null, _) => new BackendEvidence.Result(correlation, string.Empty),
                (_, "error" or "aborted") when AccountLimitDetector.Inspect("pi", "default", "cli_nonzero_exit",
                    _errorMessage, DateTimeOffset.UtcNow) is not null => new BackendEvidence.AgentError("agent_rate_limited", _errorMessage!),
                (_, "error" or "aborted") => new BackendEvidence.ProtocolError("pi_" + _stopReason),
                ({ Length: > MaxResultChars }, _) => new BackendEvidence.ProtocolError("backend_result_too_long"),
                var (text, _) => new BackendEvidence.Result(correlation, text),
            };
        }

        public BackendEvidence? FinishAtEndOfOutput()
        {
            if (_finished)
            {
                return null;
            }

            _finished = true;
            // A message_end is only one low-level response. Pi may still retry
            // or recover; only agent_settled confirms the session-level turn.
            return new BackendEvidence.ProtocolError("pi_not_settled");
        }

        static bool IsAssistant(JsonElement message) =>
            message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("role", out var role)
            && role.ValueEquals("assistant");

        static string TextOf(JsonElement message)
        {
            var text = new StringBuilder();
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("type", out var partType) && partType.ValueEquals("text")
                        && part.TryGetProperty("text", out var value))
                    {
                        text.Append(value.GetString());
                    }
                }
            }

            return text.ToString();
        }
    }

    /// <summary>Splits stdout into lines; a line over <see cref="MaxLineBytes"/> is discarded, never buffered.</summary>
    internal static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadLinesAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken, Action? onOversized = null)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        var skipping = false;
        while (true)
        {
            int read;
            Exception? failure = null;
            try
            {
                read = await stream.ReadAsync(chunk, cancellationToken);
            }
            catch (Exception ex)
            {
                read = 0;
                failure = ex;
            }
            if (read == 0)
            {
                if (skipping)
                {
                    onOversized?.Invoke();
                }
                else if (buffer.Length > 0)
                {
                    yield return buffer.ToArray();
                }

                if (failure is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
                }
                yield break;
            }

            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (chunk[i] != (byte)'\n')
                {
                    continue;
                }

                if (!skipping)
                {
                    if (buffer.Length + i - start <= MaxLineBytes)
                    {
                        buffer.Write(chunk, start, i - start);
                        yield return buffer.ToArray();
                    }
                    else
                    {
                        onOversized?.Invoke();
                    }
                }
                else
                {
                    onOversized?.Invoke();
                }

                buffer.SetLength(0);
                skipping = false;
                start = i + 1;
            }

            if (!skipping)
            {
                buffer.Write(chunk, start, read - start);
                if (buffer.Length > MaxLineBytes)
                {
                    buffer.SetLength(0);
                    skipping = true;
                }
            }
        }
    }

    static async Task DrainAsync(Stream stream)
    {
        var sink = new byte[4096];
        try
        {
            while (await stream.ReadAsync(sink) > 0)
            {
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
