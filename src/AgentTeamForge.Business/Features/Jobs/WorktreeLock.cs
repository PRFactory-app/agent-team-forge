using System.Diagnostics;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>
/// Serializes `git worktree add/remove` per repository across threads, daemons and processes on this machine.
/// Concurrent adds (or an add next to a remove) read each other's half-written admin files and fail
/// ("failed to read .git/worktrees/&lt;id&gt;/commondir").
/// The lock is the OS lock on an open handle, so it dies with its holder's process: a crash never leaves it held.
/// The file itself carries no state and is never deleted. FileShare.None is a mandatory share-mode lock on Windows and
/// an advisory flock on Linux/macOS; every ATF path takes it only through this class, so all three behave the same.
/// </summary>
public static class WorktreeLock
{
    public const string FileName = "atf-worktree.lock";
    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    // Holders are bounded by their git timeouts (checkout up to 10 min); a waiter outlasts one such holder, never forever.
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(11);

    /// <summary>Waits for the repository lock; throws OperationCanceledException on ct and TimeoutException at the deadline.</summary>
    public static IDisposable Acquire(string commonDir, CancellationToken ct, TimeSpan? timeout = null)
    {
        var path = Path.Combine(commonDir, FileName);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (TryOpen(path) is { } held) { return held; }
            ThrowAtDeadline(path, watch, timeout);
            ct.WaitHandle.WaitOne(Poll);
        }
    }

    public static async Task<IDisposable> AcquireAsync(string commonDir, CancellationToken ct, TimeSpan? timeout = null)
    {
        var path = Path.Combine(commonDir, FileName);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (TryOpen(path) is { } held) { return held; }
            ThrowAtDeadline(path, watch, timeout);
            await Task.Delay(Poll, ct);
        }
    }

    static void ThrowAtDeadline(string path, Stopwatch watch, TimeSpan? timeout)
    {
        if (watch.Elapsed >= (timeout ?? DefaultTimeout))
        {
            throw new TimeoutException($"Timed out after {watch.Elapsed.TotalSeconds:0}s waiting for the worktree lock {path}.");
        }
    }

    /// <summary>
    /// The held handle, or null while another handle holds it: EWOULDBLOCK from flock on Unix, a sharing violation on
    /// Windows (also a scanner briefly opening the file). Both are IOException and mean retry. Missing paths propagate.
    /// </summary>
    static FileStream? TryOpen(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException))
        {
            return null;
        }
    }
}
