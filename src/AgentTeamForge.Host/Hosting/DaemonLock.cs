using AgentTeamForge.DAL.Files;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text;

namespace AgentTeamForge.Host.Hosting;

/// <summary>
/// Single-instance ownership via a kernel advisory lock (flock LOCK_EX|LOCK_NB)
/// on an open descriptor. The kernel releases it when the process dies, so an
/// abruptly killed daemon never blocks restart and no PID staleness check exists.
/// </summary>
public sealed class DaemonLock : IDisposable
{
    const long WindowsLockOffset = int.MaxValue;

    readonly FileStream _stream;

    DaemonLock(FileStream stream) => _stream = stream;

    /// <summary>Returns null when another live daemon holds the lock. Never truncates or deletes the lock file.</summary>
    public static DaemonLock? TryAcquire(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, PrivateFiles.Options(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite));
        }
        catch (IOException)
        {
            // The runtime's own shared flock conflicts with a live holder's LOCK_EX.
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            // Lock a byte past the PID text so `atf stop` can still read the owner PID.
            try { stream.Lock(WindowsLockOffset, 1); }
            catch (IOException) { stream.Dispose(); return null; }
            return new DaemonLock(stream);
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

    /// <summary>Record the lock owner's PID only after ownership is established.</summary>
    public void WriteOwnerPid()
    {
        _stream.Position = 0;
        _stream.SetLength(0);
        var bytes = Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        _stream.Write(bytes);
        _stream.Flush(flushToDisk: true);
    }

    public static int? ReadOwnerPid(string path)
    {
        try
        {
            var value = Encoding.ASCII.GetString(StateDirectory.ReadPrivateFile(path));
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0 ? pid : null;
        }
        catch (StateDirectoryException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
