using AgentTeamForge.DAL.Files;
using AgentTeamForge.DAL.Migrations;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.DAL.Sqlite;

/// <summary>
/// Owns connection settings for one on-disk database. Every connection gets
/// foreign keys, synchronous=FULL and a bounded busy timeout. Normal opening
/// never creates a database: a missing file is an error, not empty new state.
/// </summary>
public sealed class JobDatabase
{
    static JobDatabase() => SQLitePCL.Batteries_V2.Init();

    readonly string _connectionString;

    JobDatabase(string path, TimeSpan busyTimeout, SqliteOpenMode mode)
    {
        Path = path;
        BusyTimeout = busyTimeout;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (int)busyTimeout.TotalSeconds),
        }.ToString();
    }

    public string Path { get; }

    public TimeSpan BusyTimeout { get; }

    /// <summary>Creates a new database file and applies the schema. Fails if it already exists.</summary>
    public static JobDatabase Create(string path, TimeSpan busyTimeout)
    {
        if (File.Exists(path))
        {
            throw new StorageException(StorageFailure.Unavailable, "database already exists");
        }

        // Create the file owner-only before SQLite writes to it; SQLite gives its -wal and -shm
        // files the database's mode, so the whole set stays private whatever the umask.
        using (new FileStream(path, PrivateFiles.Options(FileMode.CreateNew, FileAccess.Write))) { }
        var db = new JobDatabase(path, busyTimeout, SqliteOpenMode.ReadWriteCreate);
        db.Migrate(allowCreate: true);
        return new JobDatabase(path, busyTimeout, SqliteOpenMode.ReadWrite);
    }

    /// <summary>Opens an existing database, refusing unknown newer schemas without mutation.</summary>
    public static JobDatabase Open(string path, TimeSpan busyTimeout)
    {
        if (!File.Exists(path))
        {
            throw new StorageException(StorageFailure.Unavailable, "database file is missing");
        }

        var db = new JobDatabase(path, busyTimeout, SqliteOpenMode.ReadWrite);
        db.Migrate(allowCreate: false);
        return db;
    }

    /// <summary>
    /// Runs SQLite's <c>quick_check</c> on an existing database without migrating it. A damaged
    /// file throws <see cref="StorageFailure.Corrupt"/> carrying SQLite's own report.
    /// </summary>
    public static void Verify(string path, TimeSpan busyTimeout)
    {
        if (!File.Exists(path))
        {
            throw new StorageException(StorageFailure.Unavailable, "database file is missing");
        }

        try
        {
            using var connection = new JobDatabase(path, busyTimeout, SqliteOpenMode.ReadWrite).OpenConnection();
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA quick_check(10)";
            var report = new List<string>();
            using (var reader = check.ExecuteReader())
            {
                while (reader.Read())
                {
                    report.Add(reader.GetString(0));
                }
            }
            if (report is not ["ok"])
            {
                throw new StorageException(StorageFailure.Corrupt, "quick_check: " + string.Join("; ", report));
            }
        }
        catch (SqliteException ex)
        {
            throw StorageException.From(ex);
        }
    }

    /// <summary>Copies an existing database through SQLite, including committed WAL pages.</summary>
    public static void Backup(string sourcePath, string destinationPath, TimeSpan busyTimeout)
    {
        // SQLite opens an existing destination. Create it privately before
        // SQLite writes any backup pages.
        using (new FileStream(destinationPath, PrivateFiles.Options(FileMode.CreateNew, FileAccess.Write))) { }
        using var sourceConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (int)busyTimeout.TotalSeconds),
        }.ToString());
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (int)busyTimeout.TotalSeconds),
        }.ToString());
        sourceConnection.Open();
        destination.Open();
        sourceConnection.BackupDatabase(destination);
    }

    internal SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText =
                $"PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA busy_timeout={(int)BusyTimeout.TotalMilliseconds};";
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch (SqliteException ex)
        {
            connection.Dispose();
            throw StorageException.From(ex);
        }
    }

    void Migrate(bool allowCreate)
    {
        try
        {
            using var connection = OpenConnection();
            Schema.Apply(connection, allowCreate);
        }
        catch (SqliteException ex)
        {
            throw StorageException.From(ex);
        }
    }
}
