namespace AgentTeamForge.DAL.Migrations;

/// <summary>Slice 5 SQL; registration is deliberately left to the wave integrator.</summary>
public static class PRFactoryWorkspaceMigration
{
    public const string Sql = """
        CREATE TABLE prfactory_workspaces (
            workspace_key TEXT PRIMARY KEY,
            snapshot TEXT NOT NULL);
        CREATE TABLE prfactory_workspace_integrations (
            workspace_key TEXT NOT NULL REFERENCES prfactory_workspaces(workspace_key),
            member_order INTEGER NOT NULL,
            child_head TEXT NOT NULL,
            before_sha TEXT NOT NULL,
            after_sha TEXT NOT NULL,
            applied INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(workspace_key, member_order));
        """;
}
