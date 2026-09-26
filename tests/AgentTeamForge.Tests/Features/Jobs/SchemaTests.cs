using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.Tests.Support;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class SchemaTests
{
    [Fact]
    public void Newer_schema_is_refused_and_left_unchanged()
    {
        using var dir = new TempStateDir();
        var path = dir.File("jobs.db");
        JobDatabase.Create(path, TimeSpan.FromSeconds(1));
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO schema_migrations(version, applied_at) VALUES (99, 'future')";
            command.ExecuteNonQuery();
        }

        var before = File.ReadAllBytes(path);
        var ex = Assert.Throws<StorageException>(() => JobDatabase.Open(path, TimeSpan.FromSeconds(1)));

        Assert.Equal(StorageFailure.SchemaTooNew, ex.Failure);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Missing_database_is_an_error_not_new_empty_state()
    {
        using var dir = new TempStateDir();
        var path = dir.File("jobs.db");

        Assert.Throws<StorageException>(() => JobDatabase.Open(path, TimeSpan.FromSeconds(1)));
        Assert.False(File.Exists(path));
    }
}
