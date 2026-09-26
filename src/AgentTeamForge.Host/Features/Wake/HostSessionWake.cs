using System.Text;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Transport;

namespace AgentTeamForge.Host.Features.Wake;

/// <summary>Resolve the nearest agent host once, as in procinfo.resolve_nearest_host.</summary>
public static class HostSessionWake
{
    public static IpcRequest? Resolve(StateDirectory state)
    {
        var host = OperatingSystem.IsLinux() ? NearestHost() : null;
        if ((host is null && !OperatingSystem.IsLinux() || host?.Kind == "codex")
            && Environment.GetEnvironmentVariable("CODEX_THREAD_ID") is { Length: > 0 } thread)
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            return new IpcRequest
            {
                Op = IpcProtocol.WakeRegister,
                WakeKey = "codex:" + thread,
                WakeKind = "codex",
                WakeAddress = thread,
                WakeHome = home
            };
        }

        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        if (host is null)
        {
            return null;
        }

        if (host.Value.Kind == "claude")
        {
            var socket = Environment.GetEnvironmentVariable("CLAUDE_CODE_MESSAGING_SOCKET");
            var token = Environment.GetEnvironmentVariable("CLAUDE_CODE_MESSAGING_TOKEN");
            if (string.IsNullOrEmpty(socket) || string.IsNullOrEmpty(token) || !File.Exists(socket))
            {
                return null;
            }
            var name = Path.GetFileName(socket);
            if (name.EndsWith(".sock", StringComparison.Ordinal) && int.TryParse(name[..^5], out var socketPid)
                && socketPid != host.Value.Pid)
            {
                return null;
            }
            return new IpcRequest
            {
                Op = IpcProtocol.WakeRegister,
                WakeKey = "claude:" + socket,
                WakeKind = "claude",
                WakeAddress = socket,
                WakeSecret = token
            };
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
