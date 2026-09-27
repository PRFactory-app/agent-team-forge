namespace AgentTeamForge.DAL.Migrations;

/// <summary>Slice 4 schema; registration belongs to the integration patch.</summary>
public static class PRFactoryAuthorityMigration
{
    public const string Sql = """
        CREATE TABLE prfactory_authority(
            server TEXT NOT NULL, work_item_id TEXT NOT NULL,
            disposition TEXT NOT NULL CHECK(disposition IN
                ('accepted','completed','cancelled','revoked','reconciliation-needed')),
            reason TEXT, stopping INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(server,work_item_id),
            FOREIGN KEY(server,work_item_id) REFERENCES prfactory_teams(server,work_item_id));
        """;
}
