using System.ComponentModel;
using System.Diagnostics;

namespace AgentTeamForge.Business.Features.Agents.Backends;

internal static class OwnedProcessTermination
{
    public static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { } // Process exited between the check and kill.
        catch (Win32Exception ex) when (ex.NativeErrorCode == 3) { } // ESRCH.
        catch (AggregateException ex) when (ex.Flatten().InnerExceptions.All(AlreadyExited)) { }
    }

    static bool AlreadyExited(Exception ex) => ex is InvalidOperationException or Win32Exception { NativeErrorCode: 3 };
}
