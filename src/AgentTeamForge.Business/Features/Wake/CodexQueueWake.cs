using System.Diagnostics;
using System.Text.Json;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.Business.Features.Processes;
using AgentTeamForge.DAL.Features.Wake;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.Business.Features.Wake;

/// <summary>Started=false proves no codex process ran, so nothing can be presented.</summary>
public sealed record CodexSubmission(bool Started, string? SubmissionId, string? Diagnostic = null);

/// <summary>Port of verify_codex_thread and CodexMemberWake queue transport.</summary>
public sealed partial class CodexQueueWake(Func<WakeRegistration, bool>? verify = null,
    Func<WakeRegistration, string, CancellationToken, Task<bool>>? queue = null) : IWakePoster
{
    readonly Func<WakeRegistration, bool> verifyThread = verify ?? VerifyCodexThread;
    readonly Func<WakeRegistration, string, CancellationToken, Task<bool>> queueNotice = queue ?? QueueAsync;

    public Task<bool> PostAsync(WakeRegistration target, string notice, CancellationToken cancellationToken) =>
        target.Kind == "codex" && verifyThread(target)
            ? queueNotice(target, notice, cancellationToken) : Task.FromResult(false);

    public static bool VerifyCodexThread(WakeRegistration target)
    {
        if (!Path.IsPathFullyQualified(target.Home) || !Directory.Exists(target.Home))
        {
            return false;
        }

        var sessions = Path.Combine(target.Home, "sessions");
        var database = Path.Combine(target.Home, "state_5.sqlite");
        if (File.Exists(database))
        {
            try
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = database,
                    Mode = SqliteOpenMode.ReadOnly,
                    DefaultTimeout = 1
                }.ToString();
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT rollout_path, archived FROM threads WHERE id=$id";
                command.Parameters.AddWithValue("$id", target.Address);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    var rollout = reader.GetString(0);
                    if (!Path.IsPathFullyQualified(rollout))
                    {
                        return false;
                    }

                    var path = Path.GetFullPath(rollout);
                    if (reader.GetInt64(1) != 0 || path.Split(Path.DirectorySeparatorChar).Contains("archived_sessions"))
                    {
                        return false;
                    }

                    if (Path.IsPathFullyQualified(path) && Path.GetRelativePath(sessions, path) is var relative
                        && !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is SqliteException or IOException or ArgumentException) { }
        }
        if (!Directory.Exists(sessions))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(sessions, "rollout-*-" + target.Address + "*.jsonl", SearchOption.AllDirectories)
                .Any(path => !path.Split(Path.DirectorySeparatorChar).Contains("archived_sessions"));
        }
        catch (IOException) { return false; }
    }

    static async Task<bool> QueueAsync(WakeRegistration target, string notice, CancellationToken cancellationToken) =>
        (await SubmitAsync(target.Address, target.Home, notice, cancellationToken)).SubmissionId is not null;

    /// <summary>
    /// Returns the carrier's submission id, or null when no receipt was proven.
    /// Only a codex process that never started proves nothing was queued.
    /// </summary>
    public static Task<CodexSubmission> SubmitAsync(string thread, string home, string message, CancellationToken cancellationToken) =>
        SubmitAsync(thread, home, message, null, TimeSpan.FromSeconds(15), cancellationToken);

    internal static async Task<CodexSubmission> SubmitAsync(string thread, string home, string message,
        string? executable, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Process? process;
        try
        {
            var start = new ProcessStartInfo(executable ?? QueueExecutable(ToolExecutable.Resolve))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            start.ArgumentList.Add("queue"); start.ArgumentList.Add("--thread"); start.ArgumentList.Add(thread);
            start.ArgumentList.Add("--message"); start.ArgumentList.Add(message);
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("AGENT_", StringComparison.Ordinal)
                || key is "CLAUDE_CODE_MESSAGING_SOCKET" or "CLAUDE_CODE_MESSAGING_TOKEN" or "WIN_AGENT_TEAMS_SESSION_DIR").ToArray())
            {
                start.Environment.Remove(key);
            }

            start.Environment["CODEX_HOME"] = home;
            process = NonInteractiveProcess.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or BackendNotStartedException)
        {
            return new(false, null, Diagnostic(null, false, "", "", ex.GetType().Name));
        }
        catch (IOException ex)
        {
            return new(true, null, Diagnostic(null, false, "", "", ex.GetType().Name));
        }
        if (process is null)
        {
            return new(false, null, Diagnostic(null, false, "", "", "process_not_started"));
        }

        using (process)
        {
            var stdout = new System.Text.StringBuilder();
            var stderr = new System.Text.StringBuilder();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var output = CaptureTailAsync(process.StandardOutput, stdout, deadline.Token);
            var error = CaptureTailAsync(process.StandardError, stderr, deadline.Token);
            var readers = Task.WhenAll(output, error);
            string? failure = null;
            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                await readers.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
                failure = timedOut ? "timeout" : "cancelled";
            }
            catch (IOException ex)
            {
                failure = ex.GetType().Name;
            }
            if (failure is not null)
            {
                try
                {
                    if (!process.HasExited) { OwnedProcessTermination.Kill(process); }
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await process.WaitForExitAsync(cleanup.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            // Stop both readers before inspecting their buffers, including partial output on failure.
            await deadline.CancelAsync();
            try { await readers.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
            catch (TimeoutException)
            {
                // Some pipe implementations do not cancel an outstanding read promptly.
                // Keep submission bounded even if an inherited writer remains open.
                failure ??= "output_capture_timeout";
                _ = readers.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            var exitCode = process.HasExited ? (int?)process.ExitCode : null;
            string outputTail;
            string errorTail;
            lock (stdout) { outputTail = stdout.ToString(); }
            lock (stderr) { errorTail = stderr.ToString(); }
            var id = failure is null && exitCode == 0 ? SubmissionId(outputTail, thread) : null;
            return new(true, id, Diagnostic(exitCode, timedOut, outputTail, errorTail, failure ?? (id is null ? "receipt_missing" : "submitted")));
        }
    }

    static async Task CaptureTailAsync(StreamReader reader, System.Text.StringBuilder tail, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) is var count && count > 0)
        {
            lock (tail)
            {
                tail.Append(buffer, 0, count);
                if (tail.Length > 8192) { tail.Remove(0, tail.Length - 8192); }
            }
        }
    }

    internal static string Diagnostic(int? exitCode, bool timedOut, string stdout, string stderr, string outcome) =>
        $"codex queue exit_code={exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} timeout={timedOut} outcome={outcome} stdout_tail={SafeTail(stdout)} stderr_tail={SafeTail(stderr)}";

    static string SafeTail(string value)
    {
        // The capture buffer may begin halfway through a credential-bearing line.
        if (value.Length >= 8192)
        {
            var newline = value.IndexOf('\n');
            value = newline < 0 ? "[line omitted: too large]" : value[(newline + 1)..];
        }
        // Redact before truncating so a tail cannot expose the end of a recognized credential.
        value = Credential().Replace(value, "[REDACTED]");
        value = value.Length > 2048 ? value[^2048..] : value;
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";
    }

    [System.Text.RegularExpressions.GeneratedRegex("(?i)(?:(?:bearer|basic)\\s+\\S+|(?:access_token|refresh_token|id_token|api[_-]?key|token|password|secret)[\\\"']?\\s*[:=]\\s*[\\\"']?(?:(?:bearer|basic)\\s+)?[^\\s\\\"',}]+|(?:sk-|gh[pousr]_|github_pat_)[A-Za-z0-9_-]+|eyJ[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+)")]
    private static partial System.Text.RegularExpressions.Regex Credential();

    // Queue helpers need the same wrapper bypass as job launches on Linux/macOS.
    internal static string QueueExecutable(Func<string, string> resolve) =>
        OperatingSystem.IsWindows() ? WtTabControl.WindowsAgentBinary("codex") : resolve("codex");

    /// <summary>
    /// codex 0.157 and 0.160 print "Queued message &lt;id&gt; for thread &lt;thread&gt;."; a JSON submission_id is also accepted.
    /// Exit 0 without an id is not proof that the notice was queued.
    /// </summary>
    public static bool HasSubmissionId(string output, string? thread = null)
        => SubmissionId(output, thread) is not null;

    public static string? SubmissionId(string output, string? thread = null)
    {
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var text = QueuedLine().Match(line);
            if (text.Success)
            {
                if (thread is null || text.Groups[2].Value == thread) { return text.Groups[1].Value; }
                continue;
            }
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("submission_id", out var id)
                    && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    return id.GetString();
                }
            }
            catch (JsonException) { }
        }
        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^Queued message (\S+) for thread (\S+?)\.?$")]
    private static partial System.Text.RegularExpressions.Regex QueuedLine();
}
