using System.Globalization;
using System.Text;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Bounded, immediately readable raw output for each job and its follow-up chain.</summary>
public sealed class JobLogs(string stateDirectory, Action<string>? diagnostic = null)
{
    public const int MaxLogBytes = 10 * 1024 * 1024;
    public const int MaxReadBytes = 64 * 1024;
    public const int TrimmedLogBytes = MaxLogBytes / 4 * 3;
    const int PrefixBytes = 29; // "ATFLOG1 " + twenty decimal digits + newline
    readonly Lock gate = new();
    readonly string directory = Path.Combine(stateDirectory, "logs");

    /// <summary>Offsets are absolute body-byte positions, even after the tail is trimmed.</summary>
    public JobOutput Read(string jobId, long offset = 0, int maxBytes = MaxReadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (maxBytes is < 1 or > MaxReadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        lock (gate)
        {
            var path = Path.Combine(directory, jobId + ".log");
            if (!File.Exists(path))
            {
                return new JobOutput("", "", offset, offset, offset, false);
            }

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = ReadStart(file);
            var end = start + file.Length - PrefixBytes;
            var truncated = offset < start;
            var position = Math.Clamp(offset, start, end);
            var count = (int)Math.Min(maxBytes, end - position);
            var bytes = new byte[count];
            file.Position = PrefixBytes + position - start;
            file.ReadExactly(bytes);
            return new JobOutput(Encoding.UTF8.GetString(bytes), Convert.ToBase64String(bytes), position + count, start, end, truncated);
        }
    }

    /// <summary>Returns a synchronous sink used by the backend's stdout and stderr readers.</summary>
    public Action<string, ReadOnlyMemory<byte>> BeginRun(string jobId, string runId, string backend)
    {
        string? lastStream = null;
        TryAppend(jobId, Encoding.UTF8.GetBytes($"\n=== run {runId} job {jobId} backend {backend} {DateTimeOffset.UtcNow:O} ===\n"));
        return (stream, bytes) =>
        {
            if (bytes.IsEmpty)
            {
                return;
            }

            lock (gate)
            {
                if (lastStream != stream)
                {
                    TryAppendLocked(jobId, Encoding.UTF8.GetBytes($"\n[{stream}]\n"));
                    lastStream = stream;
                }

                TryAppendLocked(jobId, bytes.Span);
            }
        };
    }

    void TryAppend(string jobId, ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            TryAppendLocked(jobId, bytes);
        }
    }

    void TryAppendLocked(string jobId, ReadOnlySpan<byte> bytes)
    {
        try
        {
            Directory.CreateDirectory(directory);
            if (new DirectoryInfo(directory).LinkTarget is not null)
            {
                throw new IOException("Job log directory is a symlink");
            }

            var path = Path.Combine(directory, jobId + ".log");
            using var file = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.Read,
                UnixCreateMode = OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            if (file.Length == 0)
            {
                file.Write(Prefix(0));
            }

            file.Position = file.Length;
            file.Write(bytes);
            if (file.Length <= PrefixBytes + MaxLogBytes)
            {
                return;
            }

            // Trim to three quarters of the cap so the in-place rewrite happens once per
            // MaxLogBytes/4 of new output instead of on every append.
            var start = ReadStart(file);
            var discard = file.Length - PrefixBytes - TrimmedLogBytes;
            var buffer = new byte[MaxReadBytes];
            for (long from = PrefixBytes + discard, to = PrefixBytes; from < file.Length;)
            {
                file.Position = from;
                var read = file.Read(buffer);
                file.Position = to;
                file.Write(buffer, 0, read);
                from += read;
                to += read;
            }

            file.SetLength(PrefixBytes + TrimmedLogBytes);
            file.Position = 0;
            file.Write(Prefix(start + discard));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostic?.Invoke($"job log write failed for {jobId}: {ex.GetType().Name}");
        }
    }

    static byte[] Prefix(long start) => Encoding.ASCII.GetBytes("ATFLOG1 " + start.ToString("D20", CultureInfo.InvariantCulture) + "\n");

    static long ReadStart(FileStream file)
    {
        Span<byte> prefix = stackalloc byte[PrefixBytes];
        file.Position = 0;
        file.ReadExactly(prefix);
        if (!prefix[..8].SequenceEqual("ATFLOG1 "u8) || prefix[^1] != (byte)'\n'
            || !long.TryParse(Encoding.ASCII.GetString(prefix[8..^1]), CultureInfo.InvariantCulture, out var start))
        {
            throw new IOException("Invalid job log header");
        }

        return start;
    }
}

public sealed record JobOutput(string Text, string DataBase64, long NextOffset, long StartOffset, long EndOffset, bool Truncated);
