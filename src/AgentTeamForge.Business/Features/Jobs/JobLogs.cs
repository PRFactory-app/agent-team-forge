using AgentTeamForge.DAL.Files;
using System.Globalization;
using System.Text;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Bounded, immediately readable raw output for each job and its follow-up chain.</summary>
public sealed class JobLogs(string stateDirectory, Action<string>? diagnostic = null, bool plainOutput = false)
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
            // A daemon killed while creating the log can leave a short header.
            // The job remains inspectable; the next append initializes the file.
            if (file.Length < PrefixBytes)
            {
                return new JobOutput("", "", offset, offset, offset, false);
            }
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

    /// <summary>Pages complete captured lines. The cursor is an absolute log byte offset.</summary>
    public JobActivityPage ReadActivity(string jobId, string backend, long afterCursor = 0, int limit = JobActivity.MaxPageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterCursor);
        if (limit is < 1 or > JobActivity.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var entries = new List<ActivityEntry>();
        using var line = new MemoryStream();
        var cursor = afterCursor;
        var position = afterCursor;
        var stream = "stdout";
        while (entries.Count < limit)
        {
            var page = Read(jobId, position);
            if (page.Truncated)
            {
                // The tail was trimmed past our position (possibly between pages): restart at the new start.
                line.SetLength(0);
                cursor = page.StartOffset;
                position = page.StartOffset;
            }
            var bytes = Convert.FromBase64String(page.DataBase64);
            if (bytes.Length == 0)
            {
                break;
            }
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != (byte)'\n')
                {
                    if (line.Length <= 4 * 1024 * 1024)
                    {
                        line.WriteByte(bytes[i]);
                    }
                    continue;
                }
                var text = line.Length <= 4 * 1024 * 1024 ? Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r') : "";
                line.SetLength(0);
                if (text is "[stdout]" or "[stderr]" or "[status]")
                {
                    // The cursor stays before the tag so a resumed page re-learns the stream.
                    stream = text[1..^1];
                    continue;
                }
                foreach (var entry in JobActivity.Normalize(plainOutput ? "plain" : backend, text, stream))
                {
                    entries.Add(entry);
                }
                cursor = position + i + 1;
                if (entries.Count >= limit)
                {
                    return new JobActivityPage(entries, cursor);
                }
            }
            position = page.NextOffset;
            if (position >= page.EndOffset)
            {
                break;
            }
        }
        return new JobActivityPage(entries, cursor);
    }

    public string? LastActivity(string jobId, string backend)
    {
        try
        {
            return ReadLastActivity(jobId, backend);
        }
        catch (IOException ex)
        {
            // Older job log formats must not hide an otherwise readable page.
            diagnostic?.Invoke($"job log read failed for {jobId}: {ex}");
            return null;
        }
    }

    string? ReadLastActivity(string jobId, string backend)
    {
        var end = Read(jobId, 0, 1);
        var start = Math.Max(end.StartOffset, end.EndOffset - MaxReadBytes);
        string? last = null;
        while (start < end.EndOffset)
        {
            var page = ReadActivity(jobId, backend, start);
            if (page.Entries.Count > 0)
            {
                var text = page.Entries[^1].Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
                last = text.Length > 160 ? text[..160] + "…" : text;
            }
            if (page.NextCursor <= start)
            {
                break;
            }
            start = page.NextCursor;
        }
        return last;
    }

    public void WriteDiagnostic(string jobId, string text) => TryAppend(jobId, Encoding.UTF8.GetBytes("[status]\n" + text + "\n"));

    /// <summary>Returns a synchronous sink used by the backend's stdout and stderr readers.</summary>
    public Action<string, ReadOnlyMemory<byte>> BeginRun(string jobId, string runId, string backend)
    {
        var pending = new Dictionary<string, MemoryStream>();
        var skipping = new HashSet<string>();
        TryAppend(jobId, Encoding.UTF8.GetBytes($"\n=== run {runId} job {jobId} backend {backend} {DateTimeOffset.UtcNow:O} ===\n"));
        return (stream, bytes) =>
        {
            lock (gate)
            {
                if (!pending.TryGetValue(stream, out var line))
                {
                    line = new MemoryStream();
                    pending.Add(stream, line);
                }
                void Flush()
                {
                    TryAppendLocked(jobId, Encoding.UTF8.GetBytes($"[{stream}]\n"));
                    if (skipping.Remove(stream))
                    {
                        TryAppendLocked(jobId, "[line omitted: too large]\n"u8);
                    }
                    else
                    {
                        TryAppendLocked(jobId, line.GetBuffer().AsSpan(0, (int)line.Length));
                        TryAppendLocked(jobId, "\n"u8);
                    }
                    line.SetLength(0);
                }
                foreach (var value in bytes.Span)
                {
                    if (value == (byte)'\n')
                    {
                        Flush();
                    }
                    else if (!skipping.Contains(stream))
                    {
                        if (line.Length < MaxLogBytes)
                        {
                            line.WriteByte(value);
                        }
                        else
                        {
                            line.SetLength(0);
                            skipping.Add(stream);
                        }
                    }
                }
                if (bytes.IsEmpty && (line.Length > 0 || skipping.Contains(stream)))
                {
                    Flush();
                }
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
            using var file = new FileStream(path, PrivateFiles.Options(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read));
            if (file.Length < PrefixBytes)
            {
                file.SetLength(0);
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
