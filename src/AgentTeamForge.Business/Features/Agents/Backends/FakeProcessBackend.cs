using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentTeamForge.Business.Features.Jobs;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>
/// Explicitly selected fake backend: a real child process speaking JSON lines.
/// Uses the ordinary Process API because it gives bounded redirected streams;
/// exit status is diagnostic only and never counts as turn completion.
/// </summary>
public sealed class FakeProcessBackend(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, SpikeLimits limits)
    : IJobBackend
{
    /// <summary>Test-profile only: the fake child stalls before reading stdin.</summary>
    public const string StallBeforeReadVariable = "ATF_FAKE_STALL_BEFORE_READ";

    public IBackendRun Start(BackendRequest request)
    {
        var (behavior, hold) = ParseOptions(request.Options);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment)
        {
            info.Environment[key] = value;
        }

        if (behavior == FakeBehavior.StallBeforeRead)
        {
            info.Environment[StallBeforeReadVariable] = request.JobId;
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new BackendNotStartedException("process did not start");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            throw new BackendNotStartedException("fake backend could not be started", ex);
        }

        var line = JsonSerializer.Serialize(new FakeRequestLine(request.JobId, request.Correlation, request.Instruction, behavior, hold, request.ResumeSessionId), FakeWireJson.Default.FakeRequestLine);
        return new FakeRun(process, Encoding.UTF8.GetBytes(line + "\n"), limits);
    }

    sealed class FakeRun(Process process, byte[] requestLine, SpikeLimits limits) : IBackendRun
    {
        readonly Process _process = process;
        readonly SpikeLimits _limits = limits;
        readonly Task _stderrDrain = DrainAsync(process.StandardError.BaseStream);
        bool _deliveryFailed;

        public int? ProcessId => _process.Id;

        public async Task DeliverAsync(CancellationToken cancellationToken)
        {
            try
            {
                var stdin = _process.StandardInput.BaseStream;
                await stdin.WriteAsync(requestLine, cancellationToken);
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
                yield return new BackendEvidence.ProtocolError("backend_delivery_failed");
                yield break;
            }

            var stdout = _process.StandardOutput.BaseStream;
            var buffer = new MemoryStream();
            var chunk = new byte[4096];
            while (true)
            {
                var read = await stdout.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                {
                    yield return new BackendEvidence.EndOfOutput();
                    yield break;
                }

                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (chunk[i] != (byte)'\n')
                    {
                        continue;
                    }

                    buffer.Write(chunk, start, i - start);
                    start = i + 1;
                    var evidence = Parse(buffer.ToArray());
                    buffer.SetLength(0);
                    yield return evidence;
                    if (evidence is BackendEvidence.ProtocolError)
                    {
                        yield break;
                    }
                }

                buffer.Write(chunk, start, read - start);
                if (buffer.Length > _limits.MaxBackendLineBytes)
                {
                    yield return new BackendEvidence.ProtocolError("backend_line_too_long");
                    yield break;
                }
            }
        }

        BackendEvidence Parse(byte[] line)
        {
            if (line.Length > _limits.MaxBackendLineBytes)
            {
                return new BackendEvidence.ProtocolError("backend_line_too_long");
            }

            FakeOutputLine? message;
            try
            {
                message = JsonSerializer.Deserialize(line, FakeWireJson.Default.FakeOutputLine);
            }
            catch (JsonException)
            {
                return new BackendEvidence.ProtocolError("backend_malformed_output");
            }

            return message switch
            {
                { Type: "ack", Correlation: { } c } => new BackendEvidence.Ack(c),
                { Type: "session", Correlation: { } c, Output: { Length: > 0 and <= 256 } id } => new BackendEvidence.Session(c, id),
                { Type: "result", Correlation: { } c, Output: { } o } when o.Length <= _limits.MaxResultChars => new BackendEvidence.Result(c, o),
                _ => new BackendEvidence.ProtocolError("backend_malformed_output"),
            };
        }

        public void TerminateOwnedChild()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: false);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
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

    internal static (string Behavior, bool Hold) ParseOptions(string options)
    {
        var behavior = "complete";
        var hold = false;
        foreach (var part in options.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair is ["behavior", var b])
            {
                behavior = b;
            }
            else if (pair is ["hold", var h])
            {
                hold = h == "1";
            }
        }

        return (behavior, hold);
    }
}
