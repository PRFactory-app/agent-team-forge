using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Business.Features.Jobs;

public sealed record HumanInputRequestResult(HumanWaitRecord? Wait, string? Error, string? Instruction = null);

/// <summary>Job-scoped tool semantics. The endpoint supplies the authenticated managed job identity.</summary>
public sealed class HumanWait(HumanWaitStore waits, JobStore jobs, PRFactoryTeamStore teams, BoundPrincipal principal)
{
    public const string EndTurnInstruction = "Your question is saved. End this turn now. Do not wait on stdin, a native approval UI, or poll for an answer. The human answer will resume this saved session in a new turn.";

    public HumanInputRequestResult Request(string authenticatedJobId, string server, Guid workItemId,
        string question, string idempotencyKey, DateTimeOffset? deadline = null)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > 16000
            || string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        { return new(null, JobErrors.InvalidRequest); }
        var job = jobs.GetJob(authenticatedJobId);
        if (job is null || job.Principal != principal.Principal || job.Team != principal.Team)
        { return new(null, JobErrors.NotFound); }
        if (!SupportsBackend(job.Backend)) { return new(null, "human_wait_unsupported_backend"); }
        var member = teams.ManagedMembers(server, workItemId).SingleOrDefault(m => m.JobId == authenticatedJobId);
        if (member is null) { return new(null, JobErrors.NotFound); }
        var result = waits.Request(server, workItemId, member.Member, member.Turn, job.JobId, idempotencyKey, question, deadline);
        return new(result.Wait, result.Error, result.Error is null ? EndTurnInstruction : null);
    }

    public static string AnswerPrompt(HumanWaitRecord wait) =>
        $"[atf-human-answer:{wait.QuestionId}:{wait.AnswerCommandId:D}]\nQuestion: {wait.Question}\nHuman answer: {wait.Answer}";

    public static bool SupportsBackend(string backend) => backend is "claude" or "claude-code" or "codex" or "pi";
}
