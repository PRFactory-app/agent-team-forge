using System.Diagnostics;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Processes;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>
/// A3 startup self-test (ADR 0013): runs the sandbox each CLI uses once, without a model call, and proves that a write outside the
/// workspace and a read of a secret path fail while a write inside it works. A failed probe downgrades every Sandboxed job on that
/// CLI to full access with <see cref="SafetyDowngrade.Unavailable"/>. It never decides whether the worker reports <c>sandbox-v1</c>.
/// Codex is probed through <c>codex sandbox</c> with ATF's permission profile; Claude's sandbox runs bubblewrap with a socat proxy, so
/// its probe needs both and runs the write probe under bubblewrap. Claude's read-deny comes from ATF's settings file and is not probed.
/// </summary>
public sealed class SandboxSelfTest
{
    /// <summary>Runs one probe command in a directory; null when it could not start or did not end within the timeout.</summary>
    public delegate (int ExitCode, string Output)? Runner(string file, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout);

    const string Done = "ATF-PROBE-DONE";
    const string OutsideWritten = "ATF-PROBE-OUTSIDE-WRITTEN";
    const string SecretRead = "ATF-PROBE-SECRET-READ";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    readonly Dictionary<string, (string? Reason, string Detail)> _results;

    SandboxSelfTest(Dictionary<string, (string? Reason, string Detail)> results) => _results = results;

    /// <summary>Not run (other OS, or a test): adds no downgrade beyond <see cref="SandboxCapability.Default(string, string, string)"/>.</summary>
    public static readonly SandboxSelfTest None = new([]);

    /// <summary>The downgrade reason the self-test found for a backend; null when it passed or was not run for it.</summary>
    public string? Reason(string backend) => _results.TryGetValue(backend, out var result) ? result.Reason : null;

    /// <summary>One line per probed backend, for the daemon log and <c>atf doctor</c>.</summary>
    public IEnumerable<string> Report() => _results.Select(r => $"{r.Key}: {(r.Value.Reason is null ? "enforced" : r.Value.Reason)} ({r.Value.Detail})");

    /// <summary>The probe a job's effective mode is decided with: the static capability first, then this self-test.</summary>
    public SandboxProbe Probe(SandboxProbe? inner = null)
    {
        inner ??= SandboxCapability.Default;
        return (backend, cwd, launchMode) => inner(backend, cwd, launchMode) ?? Reason(backend);
    }

    /// <summary>Linux only: probes the installed Codex and Claude. <paramref name="resolve"/> returns a CLI's path, or null when absent.</summary>
    public static SandboxSelfTest Run(Func<string, string?> resolve, Runner run, string? home = null)
    {
        if (!OperatingSystem.IsLinux()) { return None; }
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var results = new Dictionary<string, (string? Reason, string Detail)>(StringComparer.Ordinal);
        if (resolve("codex") is { } codex)
        {
            results[BackendCatalog.Codex] = Probe(home, (work, script) => run(codex,
                ["sandbox", .. new SandboxLaunch("", "", null).CodexArguments(managedMcp: false), "--", "sh", "-c", script], work, Timeout));
        }
        if (resolve("claude") is not null)
        {
            results[BackendCatalog.Claude] = resolve("bwrap") is not { } bwrap ? (SafetyDowngrade.Unavailable, "bubblewrap is not installed")
                : resolve("socat") is null ? (SafetyDowngrade.Unavailable, "socat is not installed")
                : Probe(home, (work, script) => run(bwrap,
                    ["--ro-bind", "/", "/", "--dev", "/dev", "--proc", "/proc", "--bind", work, work, "--unshare-net", "--die-with-parent",
                        "--chdir", work, "--", "sh", "-c", script], work, Timeout), denyRead: false);
        }
        return new(results);
    }

    /// <summary>The shell probe: a write in the workspace must work, a write in HOME and a read of each secret path must fail.</summary>
    static (string? Reason, string Detail) Probe(string home, Func<string, string, (int ExitCode, string Output)?> run, bool denyRead = true)
    {
        var work = Directory.CreateTempSubdirectory("atf-sandbox-probe-").FullName;
        var outside = Path.Combine(home, $".atf-sandbox-probe-{Guid.NewGuid():N}");
        try
        {
            var secrets = denyRead ? SandboxPolicy.DenyRead(home).Select(p => Path.Combine(home, p[2..])).ToList() : [];
            var script = $"echo in > inside; echo out > {Quote(outside)} 2>/dev/null && echo {OutsideWritten}; "
                + string.Concat(secrets.Select(s => $"ls {Quote(s)} >/dev/null 2>&1 && echo {SecretRead}; "))
                + $"echo {Done}";
            if (run(work, script) is not { } result) { return (SafetyDowngrade.Unavailable, "the probe did not start or timed out"); }
            if (!result.Output.Contains(Done, StringComparison.Ordinal) || !File.Exists(Path.Combine(work, "inside")))
            {
                return (SafetyDowngrade.Unavailable, $"the sandbox did not run the probe (exit {result.ExitCode})");
            }
            if (result.Output.Contains(OutsideWritten, StringComparison.Ordinal) || File.Exists(outside))
            {
                return (SafetyDowngrade.Unavailable, "a write outside the workspace was not blocked");
            }
            return result.Output.Contains(SecretRead, StringComparison.Ordinal)
                ? (SafetyDowngrade.Unavailable, "a read of a secret path was not blocked")
                : (null, secrets.Count > 0 ? "outside write and secret reads blocked" : "outside write blocked");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (SafetyDowngrade.Unavailable, "probe setup failed: " + ex.Message);
        }
        finally
        {
            try { File.Delete(outside); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { Directory.Delete(work, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>The real runner: stdout and stderr together, the process killed at the timeout.</summary>
    public static (int ExitCode, string Output)? RunProcess(string file, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments) { info.ArgumentList.Add(argument); }
        try
        {
            using var process = NonInteractiveProcess.Start(info);
            if (process is null) { return null; }
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            // One deadline for the exit and both pipes: a descendant that keeps a pipe open must not hold the daemon start.
            var deadline = Stopwatch.StartNew();
            if (!process.WaitForExit(timeout) || !Task.WhenAll(stdout, stderr).Wait(timeout - deadline.Elapsed is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero))
            {
                OwnedProcessTermination.Kill(process);
                return null;
            }
            return (process.ExitCode, stdout.Result + stderr.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>A CLI on PATH (through the mise-aware lookup), or null when it is absent.</summary>
    public static string? Find(string name) => ToolExecutable.Resolve(name) is { } path && Path.IsPathRooted(path) ? path : null;
}
