using Microsoft.Data.Sqlite;

namespace AgentTeamForge.DAL.Migrations;

/// <summary>Slice 9a schema. The integrator registers this in Schema after the parallel migrations settle.</summary>
public static class AccountAdmissionMigration
{
    public const string Sql = """
        CREATE TABLE account_windows(
            backend TEXT NOT NULL, account_key TEXT NOT NULL,
            reason TEXT NOT NULL, observed_at TEXT NOT NULL, resets_at TEXT,
            PRIMARY KEY(backend, account_key));
        CREATE TABLE account_parks(
            job_id TEXT PRIMARY KEY, backend TEXT NOT NULL, account_key TEXT NOT NULL,
            reason TEXT NOT NULL, session_id TEXT, observed_at TEXT NOT NULL,
            resets_at TEXT, state TEXT NOT NULL DEFAULT 'parked',
            FOREIGN KEY(backend, account_key) REFERENCES account_windows(backend, account_key));
        CREATE INDEX account_parks_due ON account_parks(state, resets_at);
        CREATE TABLE account_admission_reservations(
            token TEXT PRIMARY KEY, expires_at TEXT NOT NULL);
        """;

    public static void Apply(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.ExecuteNonQuery();
    }
}
