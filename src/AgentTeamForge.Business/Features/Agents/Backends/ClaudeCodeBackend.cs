using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>
/// Headless Claude Code: one <c>claude -p --output-format stream-json --verbose</c> process per
/// turn. The instruction travels on stdin (not argv) so Start does not deliver
/// it; a follow-up resumes the native session with <c>--resume</c>. A new
/// session gets its id up front (<c>--session-id</c>), so it is recorded before
/// the turn ends and a stopped or timed-out turn can still be followed up.
/// </summary>
public sealed class ClaudeCodeBackend(string executable = "claude") : IJobBackend
{
    /// <summary>Upper bound on one stream-json line.</summary>
    public const int MaxOutputBytes = 4 * 1024 * 1024;

    public IBackendRun Start(BackendRequest request)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var sessionId = request.ResumeSessionId ?? Guid.NewGuid().ToString();
        foreach (var argument in Arguments(request, sessionId))
        {
            info.ArgumentList.Add(argument);
        }

        if (request.WorkingDirectory is { } cwd)
        {
            info.WorkingDirectory = cwd;
        }
        OrphanedBackendProcess.Mark(info, request.Correlation);

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new BackendNotStartedException("claude did not start");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            throw new BackendNotStartedException("claude could not be started", ex);
        }

        return new ClaudeRun(process, request.Correlation, sessionId, Encoding.UTF8.GetBytes(request.Instruction),
            request.ResumeSessionId is null, request.Output);
    }

    internal static List<string> Arguments(BackendRequest request, string sessionId)
    {
        List<string> arguments = ["-p", "--output-format", "stream-json", "--verbose", "--dangerously-skip-permissions"];
        foreach (var part in request.Options.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Split('=', 2) is ["model", { Length: > 0 } model])
            {
                arguments.AddRange(["--model", model]);
            }

            if (part.Split('=', 2) is ["effort", { Length: > 0 } effort])
            {
                arguments.AddRange(["--effort", effort]);
            }
        }
        arguments.AddRange(request.ResumeSessionId is null ? ["--session-id", sessionId] : ["--resume", sessionId]);
        return arguments;
    }

    sealed class ClaudeRun(Process process, string correlation, string sessionId, byte[] instruction, bool newSession,
        Action<string, ReadOnlyMemory<byte>>? output) : IBackendRun
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
                // The child started, so the effect is uncertain; surfaced as a protocol error.
                _deliveryFailed = true;
            }
        }

        public async IAsyncEnumerable<BackendEvidence> ReadEvidenceAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (_deliveryFailed)
            {
                TerminateOwnedChild();
                yield return new BackendEvidence.ProtocolError(!newSession && await BackendSessionErrors.HasExpiredDiagnosticAsync(_stderrDrain, cancellationToken)
                    ? "session_expired" : "backend_delivery_failed");
                yield break;
            }

            // Known before the turn ends: a stopped turn stays resumable.
            yield return new BackendEvidence.Session(correlation, sessionId);
            var final = Array.Empty<byte>();
            await foreach (var line in ReadLinesAsync(_stdout, cancellationToken))
            {
                if (line is null)
                {
                    output?.Invoke("status", "[stdout line omitted: too large]\n"u8.ToArray());
                    continue;
                }
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("type", out var type) && type.ValueEquals("result"))
                    {
                        final = line;
                    }
                }
                catch (JsonException) { }
            }
            // A successful turn may itself mention these phrases; only a turn without a
            // result is checked for a missing native session.
            var interpreted = Interpret(correlation, final).ToList();
            if (!newSession && !interpreted.Any(e => e is BackendEvidence.Result)
                && (BackendSessionErrors.IsExpired(Encoding.UTF8.GetString(final))
                    || await BackendSessionErrors.HasExpiredDiagnosticAsync(_stderrDrain, cancellationToken)))
            {
                yield return new BackendEvidence.ProtocolError("session_expired");
                yield break;
            }

            yield return new BackendEvidence.Ack(correlation);
            foreach (var evidence in interpreted)
            {
                if (evidence is not BackendEvidence.Session { SessionId: var reported } || reported != sessionId)
                {
                    yield return evidence;
                }
            }

            yield return new BackendEvidence.EndOfOutput();
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

        public void InterruptTurn()
        {
            if (newSession)
            {
                // --session-id is known before Claude initializes its transcript.
                // A resume sent before that file exists exits without a result.
                var root = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                var projects = Path.Combine(root, "projects");
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline && !_process.HasExited)
                {
                    try
                    {
                        if (Directory.Exists(projects) && Directory.EnumerateFiles(projects, sessionId + ".jsonl", SearchOption.AllDirectories)
                            .Any(path => new FileInfo(path).Length > 0))
                        {
                            break;
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    Thread.Sleep(100);
                }
            }

            TerminateOwnedChild();
        }

        public async ValueTask DisposeAsync()
        {
            var stdoutDrain = DrainAsync(_stdout);
            try
            {
                try
                {
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    // A closed stdout does not prove that the child has exited.
                    TerminateOwnedChild();
                    try
                    {
                        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
                    }
                    catch (TimeoutException)
                    {
                    }
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
            }
            finally
            {
                _process.Dispose();
            }
        }

        static async IAsyncEnumerable<byte[]?> ReadLinesAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            var skipping = false;
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if (chunk[i] == (byte)'\n')
                    {
                        yield return skipping ? null : buffer.ToArray();
                        buffer.SetLength(0);
                        skipping = false;
                    }
                    else if (!skipping)
                    {
                        buffer.WriteByte(chunk[i]);
                        if (buffer.Length > MaxOutputBytes)
                        {
                            buffer.SetLength(0);
                            skipping = true;
                        }
                    }
                }
            }
            if (skipping)
            {
                yield return null;
            }
            else if (buffer.Length > 0)
            {
                yield return buffer.ToArray();
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

    /// <summary>Maps claude's final result event to Session then Result or ProtocolError.</summary>
    internal static IEnumerable<BackendEvidence> Interpret(string correlation, byte[] output)
    {
        ClaudeResult? result;
        try
        {
            // Some command shims print a status line on stdout before execing claude.
            // Claude's JSON result is a single line, so only skip complete prefix lines.
            var start = 0;
            while (start < output.Length && output[start] != (byte)'{')
            {
                var newline = Array.IndexOf(output, (byte)'\n', start);
                if (newline < 0)
                {
                    break;
                }

                start = newline + 1;
            }

            result = JsonSerializer.Deserialize(output.AsSpan(start), ClaudeJson.Default.ClaudeResult);
        }
        catch (JsonException)
        {
            result = null;
        }

        if (result is null)
        {
            yield return new BackendEvidence.ProtocolError("backend_malformed_output");
            yield break;
        }

        if (!string.IsNullOrEmpty(result.SessionId))
        {
            yield return new BackendEvidence.Session(correlation, result.SessionId);
        }

        yield return result is { Type: "result", IsError: false, Result: { } text }
            ? new BackendEvidence.Result(correlation, text)
            : new BackendEvidence.ProtocolError("claude_error:" + (result.Subtype ?? "unknown"));
    }
}

/// <summary>The fields of claude's final stream-json result event that we use.</summary>
public sealed record ClaudeResult(string? Type, string? Subtype, bool IsError, string? Result, string? SessionId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ClaudeResult))]
public sealed partial class ClaudeJson : JsonSerializerContext;
