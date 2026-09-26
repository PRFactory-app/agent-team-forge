using System.Diagnostics;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>
/// Start-info builders for the installed <c>herdr</c> CLI: exact argument lists, never a shell
/// string, and an environment built only from the injected seed through <see cref="LaunchEnvironment"/>.
/// </summary>
static class HerdrCommands
{
    /// <summary>The only shell text used; the session name travels as <c>$0</c>, never inside it.</summary>
    public const string ServerScript = "exec herdr --session \"$0\" server </dev/null >/dev/null 2>&1";

    /// <summary>A CLI command; <paramref name="socketPath"/> null means a global (session-management) command.</summary>
    public static ProcessStartInfo CommandStartInfo(IReadOnlyDictionary<string, string?> seed, string? extraAllowed, string? socketPath, params string[] args)
    {
        var psi = new ProcessStartInfo("herdr")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        Seed(psi, seed, extraAllowed);
        if (socketPath is not null)
        {
            psi.Environment["HERDR_SOCKET_PATH"] = socketPath;
        }
        return psi;
    }

    /// <summary>
    /// Detached server launch (setsid, stdio to /dev/null so it outlives the caller). Refuses any
    /// name already listed by Herdr, running or stopped.
    /// </summary>
    public static ProcessStartInfo OwnedServerStartInfo(System.Text.Json.Nodes.JsonNode sessionList, string sessionName, IReadOnlyDictionary<string, string?> seed, string? extraAllowed)
    {
        if (!HerdrOwnership.CanCreate(sessionList, sessionName))
        {
            throw new InvalidOperationException($"session {sessionName} already exists; refusing to adopt it");
        }
        var psi = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/bin/sh" : "setsid") { UseShellExecute = false };
        var args = OperatingSystem.IsMacOS()
            ? new[] { "-c", ServerScript + " &", sessionName }
            : ["-f", "sh", "-c", ServerScript, sessionName];
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        Seed(psi, seed, extraAllowed);
        return psi;
    }

    static void Seed(ProcessStartInfo psi, IReadOnlyDictionary<string, string?> seed, string? extraAllowed)
    {
        psi.Environment.Clear();
        foreach (var (key, value) in seed)
        {
            psi.Environment[key] = value;
        }
        LaunchEnvironment.Apply(psi.Environment, extraAllowed);
    }
}
