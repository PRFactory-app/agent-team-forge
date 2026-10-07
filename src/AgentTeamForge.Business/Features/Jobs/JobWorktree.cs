using AgentTeamForge.DAL.Files;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using System.Text;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Business.Features.Processes;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Git worktrees are created by the daemon from a pinned submit-time HEAD.</summary>
public static partial class JobWorktree
{
    static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    // Checkout of a large repository (and LFS/post-checkout hooks) can take minutes.
    static readonly TimeSpan AddTimeout = TimeSpan.FromMinutes(10);
    const int MaxGitOutputBytes = 1024 * 1024;

    // Git's own output is already in the pipe when it exits; a hook descendant may keep it open.
    static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(1);
    static readonly TimeSpan DrainPoll = TimeSpan.FromMilliseconds(50);

    /// <summary>Test seam: holds each output reader back before its first read, as a loaded machine can.</summary>
    internal static readonly AsyncLocal<TimeSpan> ReaderDelay = new();

    /// <summary>Test seam: collects each output reader's completion, to prove a cut reader is released.</summary>
    internal static readonly AsyncLocal<List<Task>?> ReaderCompletions = new();

    public static string? Head(string cwd)
    {
        var root = Git(cwd, QueryTimeout, "rev-parse", "--show-toplevel");
        return root is null ? null : Git(cwd, QueryTimeout, "rev-parse", "--verify", "HEAD");
    }

    public static string? Branch(string cwd) => Git(cwd, QueryTimeout, "symbolic-ref", "--quiet", "--short", "HEAD");

    public static async Task<string[]?> TrackedPathsAsync(string cwd, CancellationToken ct, string? rev = null)
    {
        var output = rev is null
            ? await GitCaptureAsync(cwd, QueryTimeout, false, false, ct, "ls-files", "-z", "--full-name", "--", ":/")
            : await GitCaptureAsync(cwd, QueryTimeout, false, false, ct, "ls-tree", "-r", "-z", "--name-only", "--full-tree", rev);
        // Never certify a truncated tree. The query helper bounds captured output.
        return output is null || Encoding.UTF8.GetByteCount(output) >= MaxGitOutputBytes
            ? null : output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public static bool IsCommitSha(string value) => (value.Length is 40 or 64) && value.All(Uri.IsHexDigit);

    /// <summary>Prepare from the persisted SHA, never resolve a moving branch on recovery.</summary>
    public static bool Prepare(string repository, string path, string branch, string startingSha, CancellationToken cancellationToken = default)
    {
        if (!IsCommitSha(startingSha)) { return false; }
        if (Directory.Exists(path))
        {
            return Git(path, QueryTimeout, "rev-parse", "--show-prefix") == string.Empty &&
                Branch(path) == branch &&
                Git(path, QueryTimeout, "merge-base", "--is-ancestor", startingSha, "HEAD") is not null &&
                Git(path, QueryTimeout, "rev-parse", "--path-format=absolute", "--git-common-dir") ==
                Git(repository, QueryTimeout, "rev-parse", "--path-format=absolute", "--git-common-dir");
        }
        PrivateFiles.CreateDirectory(Path.GetDirectoryName(path)!);
        var commonDir = Git(repository, QueryTimeout, "rev-parse", "--path-format=absolute", "--git-common-dir");
        if (commonDir is null) { return false; }
        // Another job's slow checkout must not hold a cancelled attempt: the wait honours its token.
        IDisposable held;
        try
        {
            held = WorktreeLock.Acquire(commonDir, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            Console.Error.WriteLine($"[atf-daemon] git worktree add {path} not attempted: {ex.Message}");
            return false;
        }
        using (held)
        {
            // An interrupted worktree add can have already created the branch.
            var existing = Git(repository, QueryTimeout, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
            cancellationToken.ThrowIfCancellationRequested();
            if (existing is not null)
            {
                return existing == startingSha && Add(repository, "worktree", "add", path, branch);
            }
            return Add(repository, "worktree", "add", "-b", branch, path, startingSha);
        }
    }

    /// <summary>A refused checkout fails the job; keep git's reason in the daemon log rather than on its raw stderr.</summary>
    static bool Add(string repository, params string[] args)
    {
        var result = RunAsync(repository, AddTimeout, CancellationToken.None, args).GetAwaiter().GetResult();
        if (result is { ExitCode: 0 }) { return true; }
        var reason = result is null ? "timed out or could not start" : ErrorText(result) ?? $"exit {result.ExitCode}";
        Console.Error.WriteLine($"[atf-daemon] git {string.Join(' ', args)} failed: {reason}");
        return false;
    }

    public static bool Prepare(JobRecord job, CancellationToken cancellationToken = default)
    {
        if (job.WorktreePath is null)
        {
            return true;
        }

        if (Directory.Exists(job.WorktreePath))
        {
            // A follow-up and a restarted daemon must preserve the existing checkout.
            // The agent may have switched branches there; only require it to still be a worktree root.
            return job.WorktreeBase is not null && IsCommitSha(job.WorktreeBase) &&
                Git(job.WorktreePath, QueryTimeout, "rev-parse", "--show-prefix") == string.Empty &&
                Git(job.WorktreePath, QueryTimeout, "merge-base", "--is-ancestor", job.WorktreeBase, "HEAD") is not null;
        }

        if (job.ParentJobId is not null || job.Cwd is null || job.WorktreeBranch is null || job.WorktreeBase is null)
        {
            return false;
        }

        try
        {
            var parent = Path.GetDirectoryName(job.WorktreePath)!;
            PrivateFiles.CreateDirectory(parent);

            return Prepare(job.Cwd, job.WorktreePath, job.WorktreeBranch, job.WorktreeBase, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The submitted cwd's counterpart inside the worktree, so a repo subdirectory stays the working directory.
    /// Found without running git (cheap enough for job views), the way git discovers the repository:
    /// the nearest physical ancestor of the cwd that holds .git.
    /// </summary>
    public static string? WorkingDirectory(JobRecord job)
    {
        if (job.WorktreePath is null || job.Cwd is null)
        {
            return job.WorktreePath ?? job.Cwd;
        }

        var prefix = RepositoryPrefix(job.Cwd);
        var mapped = string.IsNullOrEmpty(prefix) ? job.WorktreePath : Path.Combine(job.WorktreePath, prefix);
        return Directory.Exists(mapped) ? mapped : job.WorktreePath;
    }

    static string? RepositoryPrefix(string cwd)
    {
        if (PhysicalPath.Resolve(Path.GetFullPath(cwd)) is not { } physical) { return null; }
        for (var directory = physical; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var marker = Path.Combine(directory, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                var prefix = Path.GetRelativePath(directory, physical);
                return prefix == "." ? string.Empty : prefix;
            }
        }
        return null;
    }

    /// <summary>Trimmed stdout (possibly empty) on exit code 0; otherwise null. Git's stderr is captured, never inherited.</summary>
    static string? Git(string cwd, TimeSpan timeout, params string[] args) =>
        GitAsync(cwd, timeout, CancellationToken.None, args).GetAwaiter().GetResult();

    internal static Task<string?> GitAsync(string cwd, TimeSpan timeout, CancellationToken cancellationToken, params string[] args) =>
        GitCaptureAsync(cwd, timeout, true, false, cancellationToken, args);

    /// <summary>Untrimmed stdout on exit code 0 (for -z output); null on failure.</summary>
    internal static Task<string?> GitRawAsync(string cwd, TimeSpan timeout, CancellationToken cancellationToken, params string[] args) =>
        GitCaptureAsync(cwd, timeout, false, false, cancellationToken, args);

    internal const int OutputCap = MaxGitOutputBytes;

    /// <summary>Trimmed output; with includeFailure a nonzero exit still returns its output (e.g. merge-tree conflicts).</summary>
    internal static Task<string?> GitOutputAsync(string cwd, TimeSpan timeout, bool includeFailure, CancellationToken cancellationToken, params string[] args) =>
        GitCaptureAsync(cwd, timeout, true, includeFailure, cancellationToken, args);

    static async Task<string?> GitCaptureAsync(string cwd, TimeSpan timeout, bool trim, bool includeFailure, CancellationToken cancellationToken, params string[] args) =>
        await RunAsync(cwd, timeout, cancellationToken, args) is { } result && (result.ExitCode == 0 || includeFailure)
            ? trim ? result.Output.Trim() : result.Output
            : null;

    internal sealed record GitResult(int ExitCode, string Output, string Error);

    /// <summary>Git's stderr, trimmed and bounded for a log line or result detail; null when it printed nothing.</summary>
    internal static string? ErrorText(GitResult result)
    {
        var text = result.Error.Trim();
        return text.Length == 0 ? null : text.Length <= 2000 ? text : text[..2000] + "...";
    }

    /// <summary>Exit code with bounded stdout and stderr; null when git could not run or hit the deadline.</summary>
    internal static async Task<GitResult?> RunAsync(string cwd, TimeSpan timeout, CancellationToken cancellationToken, params string[] args)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var info = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.Environment["GIT_TERMINAL_PROMPT"] = "0";
            info.ArgumentList.Add("-C");
            info.ArgumentList.Add(cwd);
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using var process = NonInteractiveProcess.Start(info);
            if (process is null)
            {
                return null;
            }

            // Each stream is read by its own thread, so a busy thread pool cannot delay reading git's output.
            // Output beyond the cap is discarded while draining continues.
            var output = new OutputReader(process.StandardOutput.BaseStream);
            var error = new OutputReader(process.StandardError.BaseStream);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                try
                {
                    OwnedProcessTermination.Kill(process);
                }
                catch (InvalidOperationException)
                {
                    // Git exited meanwhile.
                }

                await Task.WhenAll(output.ReleaseAsync(), error.ReleaseAsync());
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            // A hook descendant may keep stdout or stderr open after git exits; git's own output is already in the
            // pipe by then. A stream is cut only once its reader has waited the whole grace with nothing to read,
            // never because the reader had not run yet.
            var exited = Stopwatch.GetTimestamp();
            var both = Task.WhenAll(output.Completion, error.Completion);
            while (!(output.Settled(exited) && error.Settled(exited)) && !deadline.IsCancellationRequested)
            {
                await Task.WhenAny(both, Task.Delay(DrainPoll, deadline.Token));
            }
            // At the deadline a reader that has not caught up may leave git's output unread: no result, not a partial one.
            var complete = output.Complete(exited) && error.Complete(exited);
            await Task.WhenAll(output.ReleaseAsync(), error.ReleaseAsync());
            cancellationToken.ThrowIfCancellationRequested();
            return complete ? new(process.ExitCode, output.Text, error.Text) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Reads one redirected stream to its end on a dedicated thread, keeping up to the output cap.</summary>
    sealed class OutputReader
    {
        const long NotWaiting = -1;
        static readonly TimeSpan ReleaseLimit = TimeSpan.FromSeconds(5);
        const int PollMilliseconds = 100;
        readonly Stream _stream;
        readonly ArrayBufferWriter<byte> _kept = new();
        readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long _waitingSince = NotWaiting;
        volatile bool _released;
        SafeWaitHandle? _thread;

        public OutputReader(Stream stream)
        {
            _stream = stream;
            var delay = ReaderDelay.Value;
            new Thread(() => Read(delay)) { IsBackground = true, Name = "atf-git-output" }.Start();
            ReaderCompletions.Value?.Add(Completion);
        }

        public Task Completion => _completion.Task;

        public string Text
        {
            get
            {
                lock (_kept) { return Encoding.UTF8.GetString(_kept.WrittenSpan); }
            }
        }

        SafeHandle? Handle => _stream switch { PipeStream pipe => pipe.SafePipeHandle, FileStream file => file.SafeFileHandle, _ => null };

        /// <summary>At end of stream, or waiting for data with nothing to read for the grace since git exited.</summary>
        public bool Settled(long exited) =>
            Completion.IsCompleted
            || Interlocked.Read(ref _waitingSince) is var since && since != NotWaiting && Stopwatch.GetElapsedTime(Math.Max(since, exited)) >= DrainGrace;

        /// <summary>Settled, or waiting for data since after git exited, so everything git wrote has been read.</summary>
        public bool Complete(long exited) =>
            Settled(exited) || Interlocked.Read(ref _waitingSince) is var since && since != NotWaiting && since >= exited;

        /// <summary>
        /// Ends a reader that a lingering descendant keeps waiting, so it gives back its thread and pipe handle.
        /// On Unix the reader waits in bounded polls and sees the release itself. On Windows its pending ReadFile
        /// holds the handle through disposal, so the I/O is cancelled. Bounded: a reader that will not stop is left.
        /// </summary>
        public async Task ReleaseAsync()
        {
            _released = true;
            var watch = Stopwatch.StartNew();
            while (!Completion.IsCompleted && watch.Elapsed < ReleaseLimit)
            {
                if (OperatingSystem.IsWindows()) { CancelWindowsRead(); }
                try { await Completion.WaitAsync(DrainPoll); }
                catch (TimeoutException) { /* Not stopped yet: cancel again. */ }
            }
            _stream.Dispose();
        }

        void CancelWindowsRead()
        {
            try
            {
                if (Handle is { } handle) { Native.CancelIoEx(handle, 0); }
                if (Volatile.Read(ref _thread) is { } thread) { Native.CancelSynchronousIo(thread); }
            }
            catch (ObjectDisposedException)
            {
                // The reader finished and gave its handles back meanwhile.
            }
        }

        void Read(TimeSpan delay)
        {
            var handle = OperatingSystem.IsWindows() ? null : Handle;
            var added = false;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // CancelSynchronousIo needs this thread's own handle.
                    var thread = new SafeWaitHandle(Native.OpenThread(Native.ThreadTerminate, false, Native.GetCurrentThreadId()), true);
                    if (!thread.IsInvalid) { Volatile.Write(ref _thread, thread); } else { thread.Dispose(); }
                }
                // The fd stays ours (not closed and reused) while this thread polls it.
                handle?.DangerousAddRef(ref added);
                var fd = added ? (int)handle!.DangerousGetHandle() : -1;
                if (delay > TimeSpan.Zero) { Thread.Sleep(delay); }
                var buffer = new byte[16 * 1024];
                while (!_released)
                {
                    Interlocked.Exchange(ref _waitingSince, Stopwatch.GetTimestamp());
                    // A read(2) blocked on a pipe that a descendant holds cannot be interrupted, so Unix waits for data
                    // in bounded polls that notice a release; the read after them returns at once.
                    while (fd >= 0 && !_released && !Readable(fd)) { }
                    if (_released) { break; }
                    var n = _stream.Read(buffer);
                    Interlocked.Exchange(ref _waitingSince, NotWaiting);
                    if (n == 0) { break; }
                    lock (_kept) { _kept.Write(buffer.AsSpan(0, Math.Min(n, MaxGitOutputBytes - _kept.WrittenCount))); }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                // Released (cancelled) after a cut; what was read is kept.
            }
            finally
            {
                Interlocked.Exchange(ref _waitingSince, NotWaiting);
                if (added) { handle!.DangerousRelease(); }
                _thread?.Dispose();
                _completion.TrySetResult();
            }
        }

        /// <summary>Data, end of stream or an error to let the read report; false on timeout or an interrupted poll.</summary>
        static bool Readable(int fd)
        {
            var poll = new Native.PollFd { Fd = fd, Events = Native.PollIn };
            return Native.Poll(ref poll, 1, PollMilliseconds) switch
            {
                0 => false,
                < 0 => Marshal.GetLastPInvokeError() != Native.EINTR,
                _ => true,
            };
        }
    }

    static partial class Native
    {
        public const int ThreadTerminate = 0x0001;
        public const short PollIn = 0x0001;
        public const int EINTR = 4;

        [StructLayout(LayoutKind.Sequential)]
        public struct PollFd
        {
            public int Fd;
            public short Events;
            public short Revents;
        }

        [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
        public static partial int Poll(ref PollFd fds, nuint count, int timeout);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CancelIoEx(SafeHandle file, nint overlapped);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CancelSynchronousIo(SafeHandle thread);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial nint OpenThread(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint threadId);

        [LibraryImport("kernel32.dll")]
        public static partial uint GetCurrentThreadId();
    }
}
