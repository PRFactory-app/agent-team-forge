using Microsoft.Data.Sqlite;

namespace AgentTeamForge.DAL.Sqlite;

public enum StorageFailure
{
    /// <summary>Lock/busy contention; nothing was committed and the caller may retry.</summary>
    Busy,

    /// <summary>Database missing, unreadable, or a write failed; nothing is reported as committed.</summary>
    Unavailable,

    /// <summary>The database was written by a newer schema; it was left untouched.</summary>
    SchemaTooNew,
}

public sealed class StorageException(StorageFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public StorageFailure Failure { get; } = failure;

    internal static StorageException From(SqliteException ex) =>
        ex.SqliteErrorCode is 5 or 6
            ? new StorageException(StorageFailure.Busy, "database busy", ex)
            : new StorageException(StorageFailure.Unavailable, "database operation failed", ex);
}
