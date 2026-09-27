using System.Text;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Wake;

/// <summary>Resolve the nearest agent host once, as in procinfo.resolve_nearest_host.</summary>
public static class HostSessionWake
{
    public static IpcRequest? Resolve(StateDirectory state)
    {
        var host = OperatingSystem.IsLinux() ? NearestHost()
            : OperatingSystem.IsWindows() ? WindowsHostAncestry.NearestHost() : MacHostAncestry.NearestHost();
        if ((host is null && !OperatingSystem.IsLinux() || host?.Kind == "codex")
            && Environment.GetEnvironmentVariable("CODEX_THREAD_ID") is { Length: > 0 } thread)
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            return ForCodexThread(thread, home);
        }

        if (ClaudeChannel.Transport(ClaudeChannel.Platform) is null)
        {
            return null;
        }

        if (host is null)
        {
            return null;
        }

        if (host.Value.Kind == "claude")
        {
            return ForClaudeChannel(host, Environment.GetEnvironmentVariable("CLAUDE_CODE_MESSAGING_SOCKET"),
                Environment.GetEnvironmentVariable("CLAUDE_CODE_MESSAGING_TOKEN"));
        }

        if (host.Value.Kind == "pi")
        {
            var key = "pi:" + host.Value.Pid;
            return new IpcRequest
            {
                Op = IpcProtocol.WakeRegister,
                WakeKey = key,
                WakeKind = "pi",
                WakeAddress = Path.Combine(state.Path, "pi-wake-" + host.Value.Pid + ".jsonl")
            };
        }
        return null;
    }

    internal static IpcRequest? ForClaudeChannel((int Pid, string Kind)? host, string? address, string? token,
        string? platform = null)
    {
        var platformName = platform ?? ClaudeChannel.Platform;
        var pid = host?.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (host?.Kind != "claude" || !ClaudeChannel.Valid(address, token, pid, platformName)
            || ClaudeChannel.Transport(platformName) == "unix" && !File.Exists(address))
        {
            return null;
        }
        return new IpcRequest
        {
            Op = IpcProtocol.WakeRegister,
            WakeKey = "claude:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(address!))).ToLowerInvariant(),
            WakeKind = "claude",
            WakeAddress = address,
            WakeSecret = token,
            WakeHome = pid
        };
    }

    /// <summary>Same-user, self-reported thread ID as in the reference; Codex does not always pass it to MCP servers.</summary>
    public static IpcRequest? ForCodexThread(string? thread, string? home)
    {
        if (!Guid.TryParseExact(thread, "D", out var id) || id.ToString("D") != thread
            || string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home))
        {
            return null;
        }

        var target = new AgentTeamForge.DAL.Features.Wake.WakeRegistration("codex:" + thread, 0, "codex", thread, "", home);
        if (!CodexQueueWake.VerifyCodexThread(target))
        {
            return null;
        }

        return new IpcRequest
        {
            Op = IpcProtocol.WakeRegister,
            WakeKey = target.Key,
            WakeKind = "codex",
            WakeAddress = thread,
            WakeHome = home
        };
    }

    /// <summary>Read Codex's home from its process, never from a model-supplied tool argument.</summary>
    internal static string? CodexHome(int hostPid, string procRoot = "/proc")
    {
        if (!OperatingSystem.IsLinux())
        {
            return Environment.GetEnvironmentVariable("CODEX_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        try
        {
            var raw = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(procRoot,
                hostPid.ToString(System.Globalization.CultureInfo.InvariantCulture), "environ")));
            var variables = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var home = variables.FirstOrDefault(value => value.StartsWith("CODEX_HOME=", StringComparison.Ordinal));
            if (home is not null)
            {
                return home["CODEX_HOME=".Length..];
            }
            var userHome = variables.FirstOrDefault(value => value.StartsWith("HOME=", StringComparison.Ordinal));
            return userHome is null ? null : Path.Combine(userHome["HOME=".Length..], ".codex");
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static (int Pid, string Kind)? NearestHost(int? start = null, string procRoot = "/proc")
    {
        var pid = start ?? Environment.ProcessId;
        var visited = new HashSet<int>();
        for (var depth = 0; depth < 64 && pid > 0 && visited.Add(pid); depth++)
        {
            try
            {
                var root = Path.Combine(procRoot, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var status = File.ReadAllLines(Path.Combine(root, "status"));
                var parent = status.FirstOrDefault(line => line.StartsWith("PPid:", StringComparison.Ordinal));
                var cmdline = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "cmdline")))
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries);
                // As procinfo.host_kind: the image name (comm) first, then argv[0].
                var comm = File.ReadAllText(Path.Combine(root, "comm")).Trim().ToLowerInvariant();
                var name = cmdline.Length > 0 ? Path.GetFileName(cmdline[0]).ToLowerInvariant() : comm;
                var kind = comm is "claude" or "codex" or "pi" ? comm : name is "claude" or "codex" or "pi" ? name : null;
                if (kind is null && name is "node" or "nodejs")
                {
                    var args = string.Join(' ', cmdline).Replace('\\', '/').ToLowerInvariant();
                    kind = args.Contains("@anthropic-ai/claude-code", StringComparison.Ordinal) ? "claude"
                        : args.Contains("@openai/codex", StringComparison.Ordinal) ? "codex"
                        : args.Contains("@earendil-works/pi-coding-agent", StringComparison.Ordinal) ? "pi" : null;
                }
                if (kind is not null)
                {
                    return (pid, kind);
                }

                if (parent is null || !int.TryParse(parent.AsSpan(5).Trim(), out pid))
                {
                    break;
                }
            }
            catch (IOException) { break; }
            catch (UnauthorizedAccessException) { break; }
        }
        return null;
    }
}
