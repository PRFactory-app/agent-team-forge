using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>
/// Kills a process this process started, with its descendants. Use this instead of
/// <c>Process.Kill(entireProcessTree: true)</c>: on macOS that call SIGSTOPs the direct child before killing it, and
/// Darwin's <c>waitid(WEXITED | WNOHANG | WNOWAIT)</c> also reports stopped children, so the runtime's SIGCHLD handler
/// spins on the stopped child while holding its child-process lock. Every later <c>Process</c> call then blocks,
/// including the ones that would finish the kill, and the whole process hangs with the child stopped forever.
/// </summary>
public static partial class OwnedProcessTermination
{
    const int SigKill = 9, SigStop = 17, MaxChildren = 65_536;

    public static void Kill(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                // The direct child is never stopped (see above). It can still fork between the listing and the kill;
                // such a child escapes, as it would from the runtime's own best-effort tree kill.
                var children = DarwinChildren(process.Id);
                process.Kill();
                foreach (var (child, token) in children)
                {
                    KillDarwinDescendant(child, token);
                }
            }
            else
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { } // Process exited between the check and kill.
        catch (Win32Exception ex) when (ex.NativeErrorCode == 3) { } // ESRCH.
        catch (AggregateException ex) when (ex.Flatten().InnerExceptions.All(AlreadyExited)) { }
    }

    static bool AlreadyExited(Exception ex) => ex is InvalidOperationException or Win32Exception { NativeErrorCode: 3 };

    // A descendant is not our child, so stopping it (to freeze its own forks while its children are listed) never
    // reaches this process's SIGCHLD handler. It is not our child either, so its PID can be reused once it exits:
    // every signal rechecks the creation token captured when it was listed, and an unrelated process is left alone.
    internal static void KillDarwinDescendant(int pid, ulong token)
    {
        if (!DarwinProcess.SignalIfSame(pid, token, SigStop))
        {
            return; // Already gone, or the PID now belongs to another process.
        }

        var children = DarwinChildren(pid);
        _ = DarwinProcess.SignalIfSame(pid, token, SigKill);
        foreach (var (child, childToken) in children)
        {
            KillDarwinDescendant(child, childToken);
        }
    }

    // Read right after the listing: a child that already exited and whose PID was reused has another parent,
    // and one whose start time is unreadable cannot be told apart later, so both are skipped.
    static List<(int Pid, ulong Token)> DarwinChildren(int pid)
    {
        var children = new List<(int, ulong)>();
        foreach (var child in ChildPids(pid))
        {
            if (DarwinProcess.Info(child) is { Token: { } token } entry && entry.ParentPid == pid)
            {
                children.Add((child, token));
            }
        }
        return children;
    }

    static int[] ChildPids(int pid)
    {
        for (var capacity = 64; capacity <= MaxChildren; capacity *= 4)
        {
            var buffer = new int[capacity];
            var count = ListChildPids(pid, buffer, capacity * sizeof(int));
            if (count < capacity)
            {
                return count <= 0 ? [] : buffer[..count];
            }
        }
        return [];
    }

    [LibraryImport("libc", EntryPoint = "proc_listchildpids", SetLastError = true)]
    private static partial int ListChildPids(int pid, [Out] int[] buffer, int bufferSize);
}
