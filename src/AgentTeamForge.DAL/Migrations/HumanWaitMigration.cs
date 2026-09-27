namespace AgentTeamForge.DAL.Migrations;

/// <summary>Slice 3 migration; registration is intentionally left to the integrator.</summary>
public static class HumanWaitMigration
{
    public const string Sql = """
        CREATE TABLE human_waits(
            question_id TEXT PRIMARY KEY,
            server TEXT NOT NULL, work_item_id TEXT NOT NULL, member TEXT NOT NULL,
            turn INTEGER NOT NULL, job_id TEXT REFERENCES jobs(job_id),
            request_key TEXT NOT NULL, question TEXT NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('ending_turn','waiting','answer_reserved','resumed','applied','failed','cancelled')),
            safe_to_resume INTEGER NOT NULL DEFAULT 0,
            answer_command_id TEXT, answer TEXT, resumed_job_id TEXT REFERENCES jobs(job_id),
            deadline TEXT, error TEXT,
            UNIQUE(server,work_item_id,member,turn,request_key),
            UNIQUE(server,answer_command_id),
            FOREIGN KEY(server,work_item_id) REFERENCES prfactory_teams(server,work_item_id));
        CREATE UNIQUE INDEX human_waits_open_member ON human_waits(server,work_item_id,member)
            WHERE status IN ('ending_turn','waiting','answer_reserved');
        """;
}
