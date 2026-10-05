using AgentTeamForge.DAL.Files;
using System.Buffers;
using System.Diagnostics;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using System.Text;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.Business.Features.Processes;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Git worktrees are created by the daemon from a pinned submit-time HEAD.</summary>
public static class JobWorktree
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

                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            // A hook descendant may keep stdout or stderr open after git exits; git's own output is already in the
            // pipe by then. A stream is cut only once its reader has waited the whole grace with nothing to read,
            // never because the reader had not run yet. Until the deadline it keeps what it read.
            var exited = Stopwatch.GetTimestamp();
            var both = Task.WhenAll(output.Completion, error.Completion);
            while (!(output.Settled(exited) && error.Settled(exited)) && !deadline.IsCancellationRequested)
            {
                await Task.WhenAny(both, Task.Delay(DrainPoll, deadline.Token));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(process.ExitCode, output.Text, error.Text);
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
        readonly ArrayBufferWriter<byte> _kept = new();
        readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long _waitingSince = NotWaiting;

        public OutputReader(Stream stream)
        {
            // A blocking read wakes as soon as the pipe has data or closes; a lingering descendant only parks this
            // background thread until it closes the pipe or the process disposes the stream.
            var delay = ReaderDelay.Value;
            new Thread(() => Read(stream, delay)) { IsBackground = true, Name = "atf-git-output" }.Start();
        }

        public Task Completion => _completion.Task;

        public string Text
        {
            get
            {
                lock (_kept) { return Encoding.UTF8.GetString(_kept.WrittenSpan); }
            }
        }

        /// <summary>At end of stream, or blocked in a read with nothing to return for the grace since git exited.</summary>
        public bool Settled(long exited) =>
            Completion.IsCompleted
            || Interlocked.Read(ref _waitingSince) is var since && since != NotWaiting && Stopwatch.GetElapsedTime(Math.Max(since, exited)) >= DrainGrace;

        void Read(Stream stream, TimeSpan delay)
        {
            try
            {
                if (delay > TimeSpan.Zero) { Thread.Sleep(delay); }
                var buffer = new byte[16 * 1024];
                while (true)
                {
                    Interlocked.Exchange(ref _waitingSince, Stopwatch.GetTimestamp());
                    var n = stream.Read(buffer);
                    Interlocked.Exchange(ref _waitingSince, NotWaiting);
                    if (n == 0) { break; }
                    lock (_kept) { _kept.Write(buffer.AsSpan(0, Math.Min(n, MaxGitOutputBytes - _kept.WrittenCount))); }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The process disposed the stream after a grace cut; what was read is kept.
            }
            finally
            {
                _completion.TrySetResult();
            }
        }
    }
}
