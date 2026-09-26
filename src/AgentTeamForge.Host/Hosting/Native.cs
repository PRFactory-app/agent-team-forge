using System.Runtime.InteropServices;

namespace AgentTeamForge.Host.Hosting;

static partial class Native
{
    public const int LockExclusive = 2;
    public const int LockNonBlocking = 4;

    [LibraryImport("libc", SetLastError = true)]
    public static partial int flock(nint fd, int operation);

    [LibraryImport("libc")]
    public static partial uint geteuid();
}
