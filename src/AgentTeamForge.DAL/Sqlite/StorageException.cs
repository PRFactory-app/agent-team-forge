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

    /// <summary>SQLite reports the file damaged or not a database; the message carries SQLite's own report.</summary>
    Corrupt,
}

public sealed class StorageException(StorageFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public StorageFailure Failure { get; } = failure;

    /// <summary>For logs: a storage failure's kind plus SQLite's detail; any other fault by type name only.</summary>
    public static string Describe(Exception ex) => ex is StorageException storage ? $"{storage.Failure}: {storage.Message}" : ex.GetType().Name;

    internal static StorageException From(SqliteException ex) => ex.SqliteErrorCode switch
    {
        5 or 6 => new StorageException(StorageFailure.Busy, "database busy", ex),
        // SQLITE_CORRUPT and SQLITE_NOTADB: keep SQLite's text so the operator sees what is wrong.
        11 or 26 => new StorageException(StorageFailure.Corrupt, ex.Message, ex),
        _ => new StorageException(StorageFailure.Unavailable, $"database operation failed: {ex.Message}", ex),
    };
}
