using System.Diagnostics;
using System.Text.Json;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Agents.Terminals;
using AgentTeamForge.DAL.Features.Wake;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.Business.Features.Wake;

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
                        && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
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
        await SubmitAsync(target.Address, target.Home, notice, cancellationToken) is not null;

    /// <summary>Returns the carrier's submission id, or null when no receipt was proven.</summary>
    public static async Task<string?> SubmitAsync(string thread, string home, string message, CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var executable = OperatingSystem.IsWindows() ? WtTabControl.WindowsAgentBinary("codex") : "codex";
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
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
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            process.StandardInput.Close();
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                await Task.WhenAll(stdout, stderr);
                return process.ExitCode == 0 ? SubmissionId(await stdout, thread) : null;
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or OperationCanceledException or BackendNotStartedException)
        {
            return null;
        }
    }

    /// <summary>
    /// codex 0.157 prints "Queued message &lt;id&gt; for thread &lt;thread&gt;."; a JSON submission_id is also accepted.
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
