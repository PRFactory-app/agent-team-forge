namespace AgentTeamForge.DAL.Migrations;

/// <summary>Integration must register this SQL in the next serialized schema migration.</summary>
public static class PRFactoryPublicationMigration
{
    public const string Sql = """
        CREATE TABLE prfactory_publications (
            publication_id TEXT PRIMARY KEY,
            intent TEXT NOT NULL,
            verified_at TEXT NULL
        ) STRICT;
        """;
}
