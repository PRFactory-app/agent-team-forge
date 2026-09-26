using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>
/// Headless Codex: <c>codex exec --json</c> for a new session and
/// <c>codex exec resume &lt;id&gt;</c> for a follow-up. The instruction goes on
/// stdin (prompt argument <c>-</c>) so Start never delivers it. Permissions are
/// bypassed as in the reference backend. Options: <c>model=&lt;slug&gt;</c>.
/// </summary>
public sealed class CodexExecBackend(string executable = "codex") : IJobBackend
{
    /// <summary>Longer JSONL lines are rejected without buffering them.</summary>
    internal const int MaxLineBytes = 4 * 1024 * 1024;

    internal const int MaxResultChars = 1024 * 1024;

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
        OrphanedBackendProcess.Mark(info, request.Correlation);

        foreach (var argument in BuildArguments(request))
        {
            info.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new BackendNotStartedException("codex did not start");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            throw new BackendNotStartedException("codex could not be started", ex);
        }

        return new CodexRun(process, request.Correlation, Encoding.UTF8.GetBytes(request.Instruction), request.Output, request.ResumeSessionId is not null);
    }

    internal static List<string> BuildArguments(BackendRequest request)
    {
        // `exec resume` has no -C; the working directory is the process cwd for both forms.
        List<string> args = request.ResumeSessionId is not null ? ["exec", "resume"] : ["exec"];
        args.AddRange(["--json", "--dangerously-bypass-approvals-and-sandbox", "--skip-git-repo-check"]);
        foreach (var part in request.Options.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Split('=', 2) is ["model", var model] && model.Length > 0)
            {
                args.AddRange(["-m", model]);
            }
            if (part.Split('=', 2) is ["effort", { Length: > 0 } effort])
            {
                args.AddRange(["-c", "model_reasoning_effort=\"" + effort + "\""]);
            }
        }

        if (request.ResumeSessionId is { } id)
        {
            args.Add(id);
        }

        args.Add("-");
        return args;
    }

    sealed class CodexRun(Process process, string correlation, byte[] instruction, Action<string, ReadOnlyMemory<byte>>? output, bool resuming) : IBackendRun
    {
        readonly Process _process = process;
        readonly Stream _stdout = new CapturingReadStream(process.StandardOutput.BaseStream, "stdout", output);
        readonly Task<string> _stderrDrain = BackendSessionErrors.ReadStderrAsync(new CapturingReadStream(process.StandardError.BaseStream, "stderr", output));
        bool _deliveryFailed;

        public int? ProcessId => _process.Id;

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
                yield return new BackendEvidence.ProtocolError(resuming && await BackendSessionErrors.HasExpiredDiagnosticAsync(_stderrDrain, cancellationToken)
                    ? "session_expired" : "backend_delivery_failed");
                yield break;
            }

            var parser = new CodexEventParser(correlation);
            await foreach (var line in ReadLinesAsync(_stdout, cancellationToken))
            {
                if (line is null)
                {
                    TerminateOwnedChild();
                    yield return new BackendEvidence.ProtocolError("backend_line_too_long");
                    yield break;
                }

                foreach (var evidence in parser.Parse(line))
                {
                    if (evidence is BackendEvidence.ProtocolError)
                    {
                        TerminateOwnedChild();
                        yield return evidence;
                        yield break;
                    }

                    yield return evidence;
                }
            }

            if (resuming && await BackendSessionErrors.HasExpiredDiagnosticAsync(_stderrDrain, cancellationToken))
            {
                yield return new BackendEvidence.ProtocolError("session_expired");
                yield break;
            }

            if (parser.Finish() is { } error)
            {
                yield return error;
                yield break;
            }

            yield return new BackendEvidence.EndOfOutput();
        }

        static async IAsyncEnumerable<byte[]?> ReadLinesAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                {
                    if (buffer.Length > 0)
                    {
                        yield return buffer.ToArray();
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

                    if (buffer.Length + i - start > MaxLineBytes)
                    {
                        yield return null;
                        yield break;
                    }

                    buffer.Write(chunk, start, i - start);
                    yield return buffer.ToArray();
                    buffer.SetLength(0);
                    start = i + 1;
                }

                if (buffer.Length + read - start > MaxLineBytes)
                {
                    yield return null;
                    yield break;
                }

                buffer.Write(chunk, start, read - start);
            }
        }

        public void TerminateOwnedChild()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            var stdoutDrain = DrainAsync(_stdout);
            try
            {
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
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
}

/// <summary>
/// Maps <c>codex exec --json</c> events to evidence: thread.started → Ack +
/// Session; the last agent_message item is the turn result, emitted on
/// turn.completed; turn.failed is fatal; a stream error without a completed
/// turn is fatal at end of output. Unknown events are ignored.
/// </summary>
internal sealed class CodexEventParser(string correlation)
{
    string? _lastMessage;
    string? _error;
    bool _completed;

    public IEnumerable<BackendEvidence> Parse(byte[] line)
    {

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // Codex may print non-JSON diagnostics; they are not evidence.
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var type = String(root, "type");
            switch (type)
            {
                case "thread.started" when String(root, "thread_id") is { Length: > 0 } threadId:
                    return [new BackendEvidence.Ack(correlation), new BackendEvidence.Session(correlation, threadId)];
                case "item.completed" when root.TryGetProperty("item", out var item)
                    && item.ValueKind == JsonValueKind.Object
                    && String(item, "type") == "agent_message":
                    _lastMessage = String(item, "text") ?? _lastMessage;
                    return [];
                case "turn.completed":
                    _completed = true;
                    var output = _lastMessage ?? "";
                    return [new BackendEvidence.Result(correlation, output.Length > CodexExecBackend.MaxResultChars ? output[..CodexExecBackend.MaxResultChars] : output)];
                case "turn.failed":
                    return [new BackendEvidence.ProtocolError("codex_turn_failed")];
                case "error":
                    _error = String(root, "message") ?? "error";
                    return [];
                default:
                    return [];
            }
        }
    }

    /// <summary>At end of output: a stream error that never reached a completed turn is fatal.</summary>
    public BackendEvidence? Finish() => !_completed && _error is not null ? new BackendEvidence.ProtocolError("codex_error") : null;

    static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
