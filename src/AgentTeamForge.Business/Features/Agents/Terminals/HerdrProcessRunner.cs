using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

sealed record CapturedProcess(bool TimedOut, int ExitCode, string Stdout, bool StdoutTruncated, string Stderr, bool StderrTruncated);

/// <summary>Linux process identity: PID plus kernel start time, so a reused PID is never adopted.</summary>
sealed record ProcessIdentity(int Pid, ulong StartTicks);

/// <summary>Subprocess and /proc boundary of <see cref="HerdrTerminal"/>; substituted by a fake in tests.</summary>
interface IHerdrProcessRunner
{
    Task<CapturedProcess> CaptureAsync(ProcessStartInfo psi, TimeSpan timeout, int maxStdoutBytes, int maxStderrBytes, CancellationToken cancellationToken);

    /// <summary>Runs a launcher that detaches its payload and exits; throws when it cannot start or fails.</summary>
    Task StartDetachedAsync(ProcessStartInfo psi, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Every process whose argv is exactly <c>herdr --session NAME server</c>.</summary>
    IReadOnlyList<ProcessIdentity> FindServers(string sessionName);

    ProcessIdentity? Identity(int pid);

    int? ParentOf(int pid);

    string? EnvironmentValue(int pid, string name);
}

/// <summary>
/// Real runner: whole-operation deadline (exit plus output drain) and a byte cap per stream; output
/// beyond the cap is still drained so the child never blocks on a full pipe. Linux /proc queries.
/// </summary>
sealed class HerdrProcessRunner : IHerdrProcessRunner
{
    public async Task<CapturedProcess> CaptureAsync(ProcessStartInfo psi, TimeSpan timeout, int maxStdoutBytes, int maxStderrBytes, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {psi.FileName}");
        var stdout = CaptureAsync(p.StandardOutput.BaseStream, maxStdoutBytes, deadline.Token);
        var stderr = CaptureAsync(p.StandardError.BaseStream, maxStderrBytes, deadline.Token);
        try
        {
            // A descendant that inherited the pipes can hold them open after the child exits;
            // the drains share the exit deadline.
            await Task.WhenAll(p.WaitForExitAsync(deadline.Token), stdout, stderr);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Kill(p);
            cancellationToken.ThrowIfCancellationRequested();
            return new CapturedProcess(true, -1, "", false, "", false);
        }
        var (outText, outCut) = await stdout;
        var (errText, errCut) = await stderr;
        return new CapturedProcess(false, p.ExitCode, outText, outCut, errText, errCut);
    }

    public async Task StartDetachedAsync(ProcessStartInfo psi, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {psi.FileName}");
        try
        {
            await p.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Only the launcher itself is killed; the detached payload is not our child.
            Kill(p);
            throw new InvalidOperationException($"{psi.FileName} did not return in time");
        }
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{psi.FileName} exited {p.ExitCode}");
        }
    }

    public IReadOnlyList<ProcessIdentity> FindServers(string sessionName)
    {
        var result = new List<ProcessIdentity>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) &&
                Argv(pid) is ["herdr", "--session", var n, "server"] && n == sessionName && Identity(pid) is { } id)
            {
                result.Add(id);
            }
        }
        return result;
    }

    public ProcessIdentity? Identity(int pid) =>
        Stat(pid) is { } rest && ulong.TryParse(rest[22 - 3], NumberStyles.None, CultureInfo.InvariantCulture, out var start) ? new(pid, start) : null;

    public int? ParentOf(int pid) =>
        Stat(pid) is { } rest && int.TryParse(rest[4 - 3], NumberStyles.None, CultureInfo.InvariantCulture, out var ppid) ? ppid : null;

    public string? EnvironmentValue(int pid, string name)
    {
        var raw = Read($"/proc/{pid}/environ");
        var prefix = name + "=";
        return raw?.Split('\0').FirstOrDefault(e => e.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }

    /// <summary>Fields 3.. of /proc/PID/stat; comm (field 2) may contain spaces and ')'.</summary>
    static string[]? Stat(int pid) =>
        Read($"/proc/{pid}/stat") is { } stat && stat.LastIndexOf(')') is var close and > 0 && close + 2 < stat.Length
            ? stat[(close + 2)..].Split(' ')
            : null;

    static string[]? Argv(int pid) => Read($"/proc/{pid}/cmdline") is { Length: > 0 } raw ? raw.TrimEnd('\0').Split('\0') : null;

    static string? Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static void Kill(Process p)
    {
        try
        {
            p.Kill();
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    static async Task<(string Text, bool Truncated)> CaptureAsync(Stream stream, int max, CancellationToken cancellationToken)
    {
        var kept = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var truncated = false;
        int n;
        while ((n = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var room = (int)Math.Min(n, max - kept.Length);
            kept.Write(buffer, 0, room);
            truncated |= room < n;
        }
        return (Encoding.UTF8.GetString(kept.GetBuffer(), 0, (int)kept.Length), truncated);
    }
}
