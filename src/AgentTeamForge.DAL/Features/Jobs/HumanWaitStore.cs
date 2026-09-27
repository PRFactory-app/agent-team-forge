using AgentTeamForge.DAL.Sqlite;
using Microsoft.Data.Sqlite;

namespace AgentTeamForge.DAL.Features.Jobs;

public sealed record HumanWaitRecord(string QuestionId, string Server, Guid WorkItemId, string Member, int Turn,
    string? JobId, string RequestKey, string Question, string Status, bool SafeToResume,
    Guid? AnswerCommandId, string? Answer, string? ResumedJobId, DateTimeOffset? Deadline, string? Error);
public sealed record HumanWaitResult(HumanWaitRecord? Wait, string? Error);

/// <summary>Durable question and answer reservations. All mutable checks share the write transaction.</summary>
public sealed class HumanWaitStore(JobDatabase database)
{
    const string Columns = "question_id,server,work_item_id,member,turn,job_id,request_key,question,status,safe_to_resume,answer_command_id,answer,resumed_job_id,deadline,error";

    public HumanWaitResult Request(string server, Guid team, string member, int turn, string? jobId,
        string key, string question, DateTimeOffset? deadline = null) => Write<HumanWaitResult>((c, tx) =>
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > 16000 || string.IsNullOrWhiteSpace(key)
            || key.Length > 128 || turn < 0) { return new(null, "invalid_request"); }
        var args = Scope(server, team, member);
        var old = Read(c, tx, "server=$s AND work_item_id=$t AND member=$m AND turn=$turn AND request_key=$key",
            [.. args, ("$turn", turn), ("$key", key)]).SingleOrDefault();
        if (old is not null)
        {
            return old.Question == question && old.JobId == jobId && old.Deadline == deadline
                ? new(old, null) : new(null, "idempotency_conflict");
        }
        if (!Live(c, tx, args, jobId is null)) { return new(null, "member_closed"); }
        if (jobId is not null && Scalar(c, tx, """
            SELECT count(*) FROM prfactory_members m JOIN jobs j ON j.job_id=m.job_id
            WHERE m.server=$s AND m.work_item_id=$t AND m.member=$m AND m.turn=$turn AND m.job_id=$job
                AND j.status='running' AND NOT EXISTS(SELECT 1 FROM prfactory_members newer
                    WHERE newer.server=m.server AND newer.work_item_id=m.work_item_id AND newer.member=m.member AND newer.turn>m.turn)
            """, [.. args, ("$turn", turn), ("$job", jobId)]) != 1) { return new(null, "turn_not_running"); }
        if (Scalar(c, tx, "SELECT count(*) FROM human_waits WHERE server=$s AND work_item_id=$t AND member=$m AND status IN ('ending_turn','waiting','answer_reserved')", args) > 0)
        { return new(null, "question_already_open"); }
        var id = Guid.NewGuid().ToString("N");
        Execute(c, tx, """
            INSERT INTO human_waits(question_id,server,work_item_id,member,turn,job_id,request_key,question,status,safe_to_resume,deadline)
            VALUES ($id,$s,$t,$m,$turn,$job,$key,$question,$status,$safe,$deadline)
            """, [.. args, ("$id", id), ("$turn", turn), ("$job", jobId), ("$key", key), ("$question", question),
                ("$status", jobId is null ? "waiting" : "ending_turn"), ("$safe", jobId is null ? 1 : 0), ("$deadline", deadline?.ToString("O"))]);
        return new(Read(c, tx, "question_id=$id", ("$id", id)).Single(), null);
    });

    public HumanWaitResult ReserveAnswer(string server, Guid team, string member, string questionId,
        Guid commandId, string answer, int? maxIterations, DateTimeOffset now) => Write<HumanWaitResult>((c, tx) =>
    {
        if (commandId == Guid.Empty || string.IsNullOrWhiteSpace(answer) || answer.Length > 16000 || maxIterations is <= 0)
        { return new(null, "invalid_request"); }
        var row = Read(c, tx, "question_id=$id AND server=$s AND work_item_id=$t AND member=$m",
            [.. Scope(server, team, member), ("$id", questionId)]).SingleOrDefault();
        if (row is null) { return new(null, "question_not_found"); }
        var duplicate = Read(c, tx, "server=$s AND answer_command_id=$command", ("$s", server), ("$command", commandId.ToString("D"))).SingleOrDefault();
        if (duplicate is not null)
        {
            return duplicate.QuestionId == questionId && duplicate.Answer == answer
                ? new(duplicate, null) : new(null, "idempotency_conflict");
        }
        if (!Live(c, tx, Scope(server, team, member), row.JobId is null)) { return new(null, "member_closed"); }
        if (row.Status is not ("ending_turn" or "waiting")) { return new(null, "question_closed"); }
        if (row.JobId is not null && Scalar(c, tx, """
            SELECT count(*) FROM jobs j WHERE j.job_id=$job AND j.status IN ('running','completed','needs_reconciliation')
                AND NOT EXISTS(SELECT 1 FROM prfactory_members m WHERE m.server=$s AND m.work_item_id=$t AND m.member=$m AND m.turn>$turn)
            """, [.. Scope(server, team, member), ("$job", row.JobId), ("$turn", row.Turn)]) != 1)
        { return new(null, "stale_question"); }
        if (row.Deadline <= now) { return new(null, "wait_deadline_exceeded"); }
        // Started model turns consume iterations; queued jobs and reserved answers hold capacity only.
        // Every other admission path must use the same budget (see WIRING.md).
        if (row.JobId is not null && maxIterations is int limit && Capacity(c, tx, server, team) >= limit)
        { return new(null, "max_iterations_exceeded"); }
        Execute(c, tx, "UPDATE human_waits SET status='answer_reserved',answer_command_id=$command,answer=$answer WHERE question_id=$id",
            ("$command", commandId.ToString("D")), ("$answer", answer), ("$id", questionId));
        return new(Read(c, tx, "question_id=$id", ("$id", questionId)).Single(), null);
    });

    public HumanWaitRecord? Get(string id)
    {
        using var c = database.OpenConnection();
        return Read(c, null, "question_id=$id", ("$id", id)).SingleOrDefault();
    }

    public IReadOnlyList<HumanWaitRecord> ForTeam(string server, Guid team)
    {
        using var c = database.OpenConnection();
        return Read(c, null, "server=$s AND work_item_id=$t", ("$s", server), ("$t", team.ToString("D")));
    }

    public bool BlocksCompletion(string server, Guid team) => ForTeam(server, team).Any(r => r.Status != "applied");

    /// <summary>Other turn admissions must check this while holding the connector admission gate through mapping.</summary>
    public bool CanAdmitTurn(string server, Guid team, int? maxIterations) => Write((c, tx) =>
        maxIterations is null || Capacity(c, tx, server, team) < maxIterations);

    /// <summary>Only a completed native turn with a captured session is resumable. Restart uncertainty is not completion.</summary>
    public HumanWaitResult Refresh(string id) => Write<HumanWaitResult>((c, tx) =>
    {
        var row = Read(c, tx, "question_id=$id", ("$id", id)).SingleOrDefault();
        if (row is null) { return new(null, "question_not_found"); }
        if (row.Status is "applied" or "failed" or "cancelled") { return new(row, null); }
        if (!Live(c, tx, Scope(row.Server, row.WorkItemId, row.Member), row.JobId is null))
        {
            Execute(c, tx, "UPDATE human_waits SET status='cancelled',error='member_closed' WHERE question_id=$id", ("$id", id));
        }
        else if (row.ResumedJobId is { } resumed && Scalar(c, tx,
            "SELECT count(*) FROM jobs WHERE job_id=$job AND status IN ('failed','cancelled')", ("$job", resumed)) > 0)
        {
            Execute(c, tx, "UPDATE human_waits SET status='failed',error='resumed_turn_failed' WHERE question_id=$id", ("$id", id));
        }
        else if (row.JobId is not null && row.ResumedJobId is null)
        {
            if (Scalar(c, tx, "SELECT count(*) FROM prfactory_members WHERE server=$s AND work_item_id=$t AND member=$m AND turn>$turn",
                [.. Scope(row.Server, row.WorkItemId, row.Member), ("$turn", row.Turn)]) > 0)
            {
                Execute(c, tx, "UPDATE human_waits SET status='failed',error='stale_question',safe_to_resume=0 WHERE question_id=$id", ("$id", id));
            }
            else if (Scalar(c, tx, "SELECT count(*) FROM jobs WHERE job_id=$job AND status IN ('cancelled','failed')", ("$job", row.JobId)) > 0)
            {
                Execute(c, tx, "UPDATE human_waits SET status='failed',error='originating_turn_failed' WHERE question_id=$id", ("$id", id));
            }
            else if (Scalar(c, tx, "SELECT count(*) FROM jobs WHERE job_id=$job AND status='completed' AND session_id IS NULL", ("$job", row.JobId)) > 0)
            {
                Execute(c, tx, "UPDATE human_waits SET status='failed',error='resume_session_missing' WHERE question_id=$id", ("$id", id));
            }
            else if (Scalar(c, tx, """
                SELECT count(*) FROM jobs j WHERE job_id=$job AND status='completed' AND session_id IS NOT NULL AND session_fenced=0
                    AND EXISTS(SELECT 1 FROM runs WHERE job_id=j.job_id AND state='completed')
                    AND NOT EXISTS(SELECT 1 FROM runs WHERE job_id=j.job_id AND state='started')
                """, ("$job", row.JobId)) == 1)
            {
                Execute(c, tx, "UPDATE human_waits SET safe_to_resume=1,status=CASE WHEN status='ending_turn' THEN 'waiting' ELSE status END WHERE question_id=$id", ("$id", id));
            }
        }
        return new(Read(c, tx, "question_id=$id", ("$id", id)).Single(), null);
    });

    /// <summary>Persist resume and member mapping together, recovering a lost acceptance reply by its fixed key.</summary>
    public HumanWaitResult RecordResumed(string id, string resumedJobId) => Write<HumanWaitResult>((c, tx) =>
    {
        var row = Read(c, tx, "question_id=$id", ("$id", id)).Single();
        if (row.ResumedJobId is not null) { return new(row, row.ResumedJobId == resumedJobId ? null : "idempotency_conflict"); }
        if (row.Status != "answer_reserved" || !row.SafeToResume || row.JobId is null) { return new(row, "parent_not_ready"); }
        if (Scalar(c, tx, "SELECT count(*) FROM jobs WHERE job_id=$job AND parent_job_id=$parent AND idempotency_key=$key",
            ("$job", resumedJobId), ("$parent", row.JobId), ("$key", FollowUpKey(row))) != 1) { return new(row, "resume_not_durable"); }
        Execute(c, tx, """
            INSERT INTO prfactory_members(server,work_item_id,member,turn,job_id)
            SELECT $s,$t,$m,coalesce(max(turn),-1)+1,$job FROM prfactory_members WHERE server=$s AND work_item_id=$t AND member=$m
            """, [.. Scope(row.Server, row.WorkItemId, row.Member), ("$job", resumedJobId)]);
        Execute(c, tx, "UPDATE human_waits SET status='resumed',resumed_job_id=$job WHERE question_id=$id", ("$job", resumedJobId), ("$id", id));
        return new(Read(c, tx, "question_id=$id", ("$id", id)).Single(), null);
    });

    // Call only with native input evidence, or authenticated external mailbox read evidence.
    public bool ConfirmInput(string id, Guid commandId, string? resumedJobId) => Write((c, tx) =>
    {
        var row = Read(c, tx, "question_id=$id", ("$id", id)).SingleOrDefault();
        if (row is null || !Live(c, tx, Scope(row.Server, row.WorkItemId, row.Member), row.JobId is null)) { return false; }
        return Execute(c, tx, """
            UPDATE human_waits SET status='applied' WHERE question_id=$id AND answer_command_id=$command
                AND ((status='resumed' AND resumed_job_id=$job) OR (status='answer_reserved' AND job_id IS NULL AND $job IS NULL))
            """, ("$id", id), ("$command", commandId.ToString("D")), ("$job", resumedJobId)) == 1;
    });

    public HumanWaitResult FailAnswer(string id, string error) => Write<HumanWaitResult>((c, tx) =>
    {
        Execute(c, tx, "UPDATE human_waits SET status='failed',error=$error WHERE question_id=$id AND status='answer_reserved'",
            ("$id", id), ("$error", error));
        return new(Read(c, tx, "question_id=$id", ("$id", id)).SingleOrDefault(), error);
    });

    /// <summary>
    /// Freezes one state notice per question/status with the next sequence of its stream agent before HTTP,
    /// so a lost acknowledgement replays identical bytes. Returns notices not yet acknowledged.
    /// </summary>
    public IReadOnlyList<(string QuestionId, string Status, long Seq, string Batch)> PendingNotices(string server, Guid team,
        IEnumerable<(HumanWaitRecord Wait, string Agent)> current, Func<HumanWaitRecord, string, long, string> batch) => Write((c, tx) =>
    {
        foreach (var (wait, agent) in current)
        {
            var seq = Scalar(c, tx, "SELECT coalesce(max(seq),0)+1 FROM prfactory_human_stream WHERE server=$s AND work_item_id=$t AND agent=$a",
                ("$s", server), ("$t", team.ToString("D")), ("$a", agent));
            Execute(c, tx, """
                INSERT OR IGNORE INTO prfactory_human_stream(server,work_item_id,agent,question_id,status,seq,batch)
                VALUES ($s,$t,$a,$q,$status,$seq,$batch)
                """, ("$s", server), ("$t", team.ToString("D")), ("$a", agent), ("$q", wait.QuestionId), ("$status", wait.Status),
                ("$seq", seq), ("$batch", batch(wait, agent, seq)));
        }
        using var cmd = Command(c, tx, "SELECT question_id,status,seq,batch FROM prfactory_human_stream WHERE server=$s AND work_item_id=$t AND uploaded=0 ORDER BY agent,seq",
            ("$s", server), ("$t", team.ToString("D")));
        using var r = cmd.ExecuteReader();
        var rows = new List<(string, string, long, string)>();
        while (r.Read()) { rows.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3))); }
        return (IReadOnlyList<(string, string, long, string)>)rows;
    });

    public void NoticeUploaded(string questionId, string status) => Write((c, tx) =>
        Execute(c, tx, "UPDATE prfactory_human_stream SET uploaded=1 WHERE question_id=$q AND status=$status", ("$q", questionId), ("$status", status)));

    public static string FollowUpKey(HumanWaitRecord row) => "prf-human:" + row.AnswerCommandId!.Value.ToString("N");

    static long Capacity(SqliteConnection c, SqliteTransaction tx, string server, Guid team) => Scalar(c, tx, """
        SELECT (SELECT count(DISTINCT j.job_id) FROM prfactory_members m JOIN jobs j ON j.job_id=m.job_id
            WHERE m.server=$s AND m.work_item_id=$t AND (j.status IN ('queued','running')
                OR EXISTS(SELECT 1 FROM runs r WHERE r.job_id=j.job_id AND (r.acked=1 OR r.submitted_at IS NOT NULL OR r.state='completed'))))
            + (SELECT count(*) FROM human_waits w WHERE w.server=$s AND w.work_item_id=$t AND w.job_id IS NOT NULL
                AND w.status='answer_reserved')
        """, ("$s", server), ("$t", team.ToString("D")));

    static bool Live(SqliteConnection c, SqliteTransaction tx, (string, object?)[] args, bool external) =>
        Scalar(c, tx, "SELECT count(*) FROM prfactory_teams WHERE server=$s AND work_item_id=$t AND state='claimed' AND acceptance_state IN ('accepted','legacy')", args) == 1
        && Scalar(c, tx, external
            ? "SELECT count(*) FROM prfactory_external WHERE server=$s AND work_item_id=$t AND member=$m AND closed=0"
            : "SELECT count(*) FROM prfactory_killed_members WHERE server=$s AND work_item_id=$t AND member=$m", args) == (external ? 1 : 0);

    static (string, object?)[] Scope(string server, Guid team, string member) => [("$s", server), ("$t", team.ToString("D")), ("$m", member)];

    T Write<T>(Func<SqliteConnection, SqliteTransaction, T> action)
    {
        try
        {
            using var c = database.OpenConnection();
            using var tx = c.BeginTransaction();
            var result = action(c, tx);
            tx.Commit();
            return result;
        }
        catch (SqliteException ex) { throw StorageException.From(ex); }
    }

    static List<HumanWaitRecord> Read(SqliteConnection c, SqliteTransaction? tx, string where, params (string, object?)[] args)
    {
        using var cmd = Command(c, tx, "SELECT " + Columns + " FROM human_waits WHERE " + where, args);
        using var r = cmd.ExecuteReader();
        var rows = new List<HumanWaitRecord>();
        while (r.Read())
        {
            rows.Add(new(r.GetString(0), r.GetString(1), Guid.Parse(r.GetString(2)), r.GetString(3), r.GetInt32(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetInt32(9) != 0,
                r.IsDBNull(10) ? null : Guid.Parse(r.GetString(10)), r.IsDBNull(11) ? null : r.GetString(11),
                r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : DateTimeOffset.Parse(r.GetString(13)), r.IsDBNull(14) ? null : r.GetString(14)));
        }
        return rows;
    }

    static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) { cmd.Parameters.AddWithValue(name, value ?? DBNull.Value); }
        return cmd;
    }
    static long Scalar(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(c, tx, sql, args);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
    static int Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(c, tx, sql, args);
        return cmd.ExecuteNonQuery();
    }
}
