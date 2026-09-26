using System.Runtime.InteropServices;

namespace AgentTeamForge.Host.Hosting;

/// <summary>
/// Single-instance ownership via a kernel advisory lock (flock LOCK_EX|LOCK_NB)
/// on an open descriptor. The kernel releases it when the process dies, so an
/// abruptly killed daemon never blocks restart and no PID staleness check exists.
/// </summary>
public sealed class DaemonLock : IDisposable
{
    readonly FileStream _stream;

    DaemonLock(FileStream stream) => _stream = stream;

    /// <summary>Returns null when another live daemon holds the lock. Never truncates or deletes the lock file.</summary>
    public static DaemonLock? TryAcquire(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.ReadWrite,
                UnixCreateMode = StateDirectory.PrivateFile,
            });
        }
        catch (IOException)
        {
            // The runtime's own shared flock conflicts with a live holder's LOCK_EX.
            return null;
        }

        var fd = stream.SafeFileHandle.DangerousGetHandle();
        if (Native.flock(fd, Native.LockExclusive | Native.LockNonBlocking) != 0)
        {
            _ = Marshal.GetLastPInvokeError();
            stream.Dispose();
            return null;
        }

        return new DaemonLock(stream);
    }

    public void Dispose() => _stream.Dispose();
}
