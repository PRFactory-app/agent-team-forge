using System.Text;
using System.Text.Json;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Jobs;

namespace AgentTeamForge.Host.Features.FakeBackend;

/// <summary>
/// The deterministic fake agent process. Reads one request line, records a
/// test-owned receipt, acks, optionally waits on a release barrier file, then
/// behaves as instructed. Barrier waits are bounded.
/// </summary>
public static class FakeBackendCommand
{
    public const string BarrierDirVariable = "ATF_FAKE_BARRIER_DIR";
    static readonly TimeSpan BarrierDeadline = TimeSpan.FromSeconds(120);

    public static int Run()
    {
        if (Environment.GetEnvironmentVariable(FakeProcessBackend.StallBeforeReadVariable) is { Length: > 0 } stalledJob)
        {
            // Never reads stdin: the daemon's deadline must bound delivery and kill this child.
            if (Environment.GetEnvironmentVariable(BarrierDirVariable) is { } dir)
            {
                File.WriteAllText(Path.Combine(dir, $"stalled-{stalledJob}"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            Thread.Sleep(BarrierDeadline);
            return 4;
        }

        var line = ReadBoundedLine(Console.OpenStandardInput(), 64 * 1024);
        if (line is null)
        {
            return 3;
        }

        var request = JsonSerializer.Deserialize(line, FakeWireJson.Default.FakeRequestLine);
        if (request is null)
        {
            return 3;
        }

        var barrierDir = Environment.GetEnvironmentVariable(BarrierDirVariable);
        if (barrierDir is not null)
        {
            File.AppendAllText(Path.Combine(barrierDir, "invocations.log"), $"{request.JobId} {request.Correlation}\n");
        }

        if (request.Behavior == FakeBehavior.ExitAfterReceipt)
        {
            return 0;
        }

        var stdout = Console.OpenStandardOutput();
        Emit(stdout, new FakeOutputLine("ack", request.Correlation, null));
        // A follow-up echoes the resumed session, proving the daemon passed it through.
        Emit(stdout, new FakeOutputLine("session", request.Correlation, request.ResumeSessionId ?? "fake-session-" + request.JobId));
        if (barrierDir is not null)
        {
            File.WriteAllText(Path.Combine(barrierDir, $"acked-{request.JobId}"), request.Correlation);
        }

        if (request.Hold || request.Behavior == FakeBehavior.Hang)
        {
            if (!WaitForRelease(barrierDir, request.JobId, request.Behavior == FakeBehavior.Hang))
            {
                return 4;
            }
        }

        switch (request.Behavior)
        {
            case FakeBehavior.EofAfterAck:
                return 0;
            case FakeBehavior.MismatchedCorrelation:
                Emit(stdout, new FakeOutputLine("result", "not-" + request.Correlation, "stale"));
                return 0;
            default:
                Emit(stdout, new FakeOutputLine("result", request.Correlation, "fake-result: " + request.Instruction));
                return 0;
        }
    }

    static bool WaitForRelease(string? barrierDir, string jobId, bool never)
    {
        var until = DateTime.UtcNow + BarrierDeadline;
        var release = barrierDir is null ? null : Path.Combine(barrierDir, $"release-{jobId}");
        while (DateTime.UtcNow < until)
        {
            if (!never && release is not null && File.Exists(release))
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return false;
    }

    static void Emit(Stream stdout, FakeOutputLine line)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, FakeWireJson.Default.FakeOutputLine) + "\n");
        stdout.Write(bytes);
        stdout.Flush();
    }

    static string? ReadBoundedLine(Stream input, int maxBytes)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (input.Read(one) == 1)
        {
            if (one[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(buffer.ToArray());
            }

            if (buffer.Length >= maxBytes)
            {
                return null;
            }

            buffer.WriteByte(one[0]);
        }

        return null;
    }
}
