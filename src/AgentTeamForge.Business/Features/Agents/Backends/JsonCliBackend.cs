using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>Common single-result JSON process protocol used by Cursor and Droid.</summary>
internal static class JsonCliBackend
{
    const int MaxOutputBytes = 4 * 1024 * 1024;
    const int MaxResultChars = 1024 * 1024;

    internal static IBackendRun Start(string executable, string name, BackendRequest request, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (request.WorkingDirectory is { } cwd) { info.WorkingDirectory = cwd; }
        foreach (var arg in args) { info.ArgumentList.Add(arg); }
        ManagedChildContext.ClearInheritedIdentity(info);
        OrphanedBackendProcess.Mark(info, request.Correlation);
        WindowsCliLaunch.Configure(info, name, executable == name);
        try
        {
            var process = Process.Start(info) ?? throw new BackendNotStartedException($"{name} did not start");
            return new Run(process, name, request);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            throw new BackendNotStartedException($"{name} could not be started", ex);
        }
    }

    sealed class Run(Process process, string name, BackendRequest request) : IBackendRun
    {
        readonly Stream _stdout = new CapturingReadStream(process.StandardOutput.BaseStream, "stdout", request.Output);
        readonly Task<string> _stderr = BackendSessionErrors.ReadStderrAsync(
            new CapturingReadStream(process.StandardError.BaseStream, "stderr", request.Output));
        bool _deliveryFailed;

        public int? ProcessId => process.Id;
        public bool OwnedChildAlive => !process.HasExited;

        public async Task DeliverAsync(CancellationToken cancellationToken)
        {
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(request.Instruction), cancellationToken);
                await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
                process.StandardInput.Close();
            }
            catch (IOException) { _deliveryFailed = true; }
        }

        public async IAsyncEnumerable<BackendEvidence> ReadEvidenceAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (_deliveryFailed)
            {
                TerminateOwnedChild();
                yield return new BackendEvidence.ProtocolError("backend_delivery_failed");
                yield break;
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            var oversized = false;
            int read;
            while ((read = await _stdout.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read <= MaxOutputBytes) { buffer.Write(chunk, 0, read); }
                else { oversized = true; }
            }
            await process.WaitForExitAsync(cancellationToken);
            var stderr = await _stderr.WaitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                if (request.ResumeSessionId is not null && BackendSessionErrors.IsExpired(stderr))
                {
                    yield return new BackendEvidence.ProtocolError("session_expired");
                    yield break;
                }
                yield return new BackendEvidence.AgentError(name + "_error", stderr.Length > 0 ? stderr : $"{name} exited with code {process.ExitCode}");
                yield break;
            }
            if (oversized)
            {
                yield return new BackendEvidence.ProtocolError("backend_malformed_output");
                yield break;
            }
            var evidence = Interpret(request.Correlation, buffer.ToArray(), request.ResumeSessionId);
            foreach (var item in evidence) { yield return item; }
        }

        public void TerminateOwnedChild() => OwnedProcessTermination.Kill(process);

        public async ValueTask DisposeAsync()
        {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { TerminateOwnedChild(); }
            process.Dispose();
        }
    }

    internal static IReadOnlyList<BackendEvidence> Interpret(string correlation, byte[] output, string? expectedSession)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)
                || type.GetString() != "result")
            {
                return [new BackendEvidence.ProtocolError("backend_malformed_output")];
            }
            var session = root.TryGetProperty("session_id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            if (expectedSession is not null && session != expectedSession)
            {
                return [new BackendEvidence.ProtocolError("session_expired")];
            }
            if (root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True)
            {
                return [new BackendEvidence.AgentError("agent_error", root.TryGetProperty("result", out var detail) ? detail.ToString() : "Agent reported an error")];
            }
            if (root.TryGetProperty("subtype", out var subtype) && subtype.ValueKind == JsonValueKind.String && subtype.GetString() != "success")
            {
                return [new BackendEvidence.AgentError("agent_error", subtype.GetString() ?? "Agent reported an error")];
            }
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(session))
            {
                return [new BackendEvidence.ProtocolError("backend_malformed_output")];
            }
            var value = result.GetString()!;
            return [new BackendEvidence.Ack(correlation), new BackendEvidence.Session(correlation, session),
                new BackendEvidence.Result(correlation, value.Length > MaxResultChars ? value[..MaxResultChars] : value)];
        }
        catch (JsonException)
        {
            return [new BackendEvidence.ProtocolError("backend_malformed_output")];
        }
    }
}
