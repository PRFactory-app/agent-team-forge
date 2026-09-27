using System.Text.Json;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Host.Features.PRFactory;

public sealed partial class PRFactoryWorkItems
{
    /// <summary>
    /// Advances every open wait under authority: reserved answers resume the saved session once, a completed
    /// resumed turn marks the answer applied, and each state change is streamed as a frozen, replayable notice.
    /// </summary>
    async Task AdvanceHumanWaitsAsync(PRFactoryWorkItem item, CancellationToken ct)
    {
        if (interaction is null || humanWaits is null)
        {
            return;
        }
        foreach (var wait in humanWaits.ForTeam(server, item.Id).Where(w => w.Status is not ("applied" or "failed" or "cancelled")))
        {
            // Refresh, follow-up acceptance and RecordResumed stay inside one admitted effect.
            await Guard(item.Id, () =>
            {
                interaction.Advance(wait.QuestionId);
                return Task.CompletedTask;
            }, ct);
            // The backend acknowledged and completed the resumed turn whose instruction was the exact answer
            // prompt in the saved session. Native transcript scanning (stronger evidence) is not wired yet.
            if (humanWaits.Get(wait.QuestionId) is { Status: "resumed", ResumedJobId: { } resumed, AnswerCommandId: { } command }
                && getJob(resumed)?.Status == JobStatus.Completed)
            {
                humanWaits.ConfirmInput(wait.QuestionId, command, resumed);
            }
        }
        var current = humanWaits.ForTeam(server, item.Id).Select(w => (w, w.Member + ":questions"));
        foreach (var (questionId, status, seq, frozen) in humanWaits.PendingNotices(server, item.Id, current, (wait, agent, seq) => Notice(item, wait, agent, seq)))
        {
            var batch = WithoutLegacyLifecycle(JsonSerializer.Deserialize(frozen, PRFactoryWorkItemJson.Default.PRFactoryStreamBatch)!);
            PRFactoryStreamResponse response = null!;
            await Guard(item.Id, async () => response = await client.UploadStreamAsync(item.Id, batch, ct), ct);
            var agentName = batch.Lines[0].AgentName;
            if (!response.AcceptedThroughSeq.TryGetValue(agentName, out var accepted) || accepted < seq)
            {
                throw new HttpRequestException("PRFactory did not acknowledge human wait notice");
            }
            humanWaits.NoticeUploaded(questionId, status);
        }
    }

    string Notice(PRFactoryWorkItem item, HumanWaitRecord wait, string agent, long seq)
    {
        var state = HumanWaitLifecycle(wait.Status);
        var text = JsonSerializer.Serialize(new PRFactoryHumanWaitNotice(wait.QuestionId, wait.Question, wait.Status,
            wait.AnswerCommandId, wait.Error), PRFactoryWorkItemJson.Default.PRFactoryHumanWaitNotice);
        var backend = wait.JobId is null ? "external" : getJob(wait.JobId)?.Backend ?? "";
        return JsonSerializer.Serialize(new PRFactoryStreamBatch(item.LeaseToken, $"human:{wait.QuestionId}:{wait.Status}",
            [new("member", item.Id.ToString("D"), wait.Member, backend, state, item.RepositoryId, wait.Member == "lead" ? null : "lead")],
            [new(agent, seq, DateTimeOffset.UtcNow, "Record", text, "human-wait")]), PRFactoryWorkItemJson.Default.PRFactoryStreamBatch);
    }

    // PRFactory's lifecycle enum is deliberately coarser than ATF's durable question status.
    internal static string HumanWaitLifecycle(string status) => status switch
    {
        "ending_turn" or "waiting" or "answer_reserved" => "Waiting",
        "resumed" or "applied" => "Running",
        _ => "Failed"
    };

    // Notices frozen before the lifecycle mapping carry states PRFactory rejected with 400, so none was stored.
    internal static PRFactoryStreamBatch WithoutLegacyLifecycle(PRFactoryStreamBatch batch) => batch with
    {
        Events = [.. batch.Events.Select(e => e with
        {
            State = e.State switch
            {
                "WaitingForHuman" or "AnswerQueued" => "Waiting",
                "AnswerApplied" => "Running",
                var state => state
            }
        })]
    };

    /// <summary>Open waits hold completion; a failed or cancelled wait finishes the team as a failure.</summary>
    HumanWaitRecord? BlockingWait(Guid id, out bool failed)
    {
        var blocking = humanWaits?.ForTeam(server, id).OrderByDescending(w => w.Status is "failed" or "cancelled")
            .FirstOrDefault(w => w.Status != "applied");
        failed = blocking?.Status is "failed" or "cancelled";
        return blocking;
    }

    static int? MaxIterations(PRFactoryWorkItem item, string member) => item.TeamPlan?.Members
        .FirstOrDefault(m => member == "lead" ? m.IsLead : m.Name == member)?.MaxIterations;
}
