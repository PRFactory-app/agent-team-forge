using System.Text.Json;
using AgentTeamForge.Business.Features.External;
using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Host.Features.PRFactory;

/// <summary>Connector-side answer queue. Wire under the same authority/admission gate as ordinary commands.</summary>
public sealed class PRFactoryInteraction(HumanWaitStore waits, PRFactoryTeamStore teams, JobStore jobs,
    Func<FollowUpRequest, JobResult> followUp, ExternalTeam? external = null)
{
    public HumanWaitResult Answer(string server, Guid workItemId, string member, string questionId,
        Guid commandId, string answer, int? maxIterations, DateTimeOffset now)
    {
        if (commandId == Guid.Empty || string.IsNullOrWhiteSpace(answer) || answer.Length > 16000 || maxIterations is <= 0)
        { return new(null, JobErrors.InvalidRequest); }
        return waits.ReserveAnswer(server, workItemId, member, questionId, commandId, answer, maxIterations, now);
    }

    // Repeat on startup and each active-team tick. Accept and RecordResumed may be separated by a crash.
    public HumanWaitResult Advance(string questionId)
    {
        var refreshed = waits.Refresh(questionId);
        if (refreshed.Wait is not { } row || refreshed.Error is not null) { return refreshed; }
        if (row.Status != "answer_reserved" || !row.SafeToResume) { return new(row, row.Error); }
        if (row.JobId is null)
        {
            var member = teams.External(row.Server, row.WorkItemId, row.Member);
            if (external is null || member is null) { return new(row, "external_unavailable"); }
            var sent = external.SendToMemberOnce(member.TeamId, member.ActualName, HumanWait.AnswerPrompt(row), "prfactory", row.AnswerCommandId!.Value.ToString("D"));
            // Enqueue is not consumption; leave reserved until authenticated read/ack evidence.
            return new(row, sent.Error);
        }
        if (jobs.GetJob(row.JobId) is not { } parent || !HumanWait.SupportsBackend(parent.Backend))
        { return waits.FailAnswer(questionId, "human_wait_unsupported_backend"); }
        var result = followUp(new(row.JobId, HumanWait.AnswerPrompt(row), HumanWaitStore.FollowUpKey(row)));
        if (result.Error is not null)
        {
            return result.Error is JobErrors.QueueFull or JobErrors.StorageBusy or JobErrors.StorageUnavailable
                or JobErrors.DaemonUnhealthy or JobErrors.ParentNotReady
                ? new(row, result.Error) : waits.FailAnswer(questionId, result.Error);
        }
        return waits.RecordResumed(questionId, result.Job!.JobId);
    }

    /// <summary>
    /// request_human_input for a managed child authenticated as live member "child-&lt;root job&gt;". The asking
    /// turn is the root's one running descendant owned by a PRFactory team; scope comes from durable mapping.
    /// </summary>
    public static HumanInputRequestResult RequestFromManagedChild(HumanWait humanWait, JobStore jobs, PRFactoryAuthorityStore owners,
        string? memberName, string? question, string? key)
    {
        const string prefix = "child-";
        if (memberName is null || !memberName.StartsWith(prefix, StringComparison.Ordinal)
            || owners.OwnerOf(memberName[prefix.Length..]) is not { } owner)
        {
            return new(null, JobErrors.NotFound);
        }
        var root = memberName[prefix.Length..];
        var running = owners.OwnedTurns(owner.Server, owner.WorkItemId).Select(jobs.GetJob)
            .Where(job => job is { Status: JobStatus.Running } && RootOf(jobs, job) == root).ToList();
        return running.Count != 1 ? new(null, "turn_not_running")
            : humanWait.Request(running[0]!.JobId, owner.Server, owner.WorkItemId, question ?? "", key ?? "");
    }

    static string RootOf(JobStore jobs, JobRecord? job)
    {
        while (job?.ParentJobId is { } parent) { job = jobs.GetJob(parent); }
        return job?.JobId ?? "";
    }

    public bool BlocksCompletion(string server, Guid workItemId) => waits.BlocksCompletion(server, workItemId)
        || teams.PendingCommands(server, workItemId).Count != 0;

    /// <summary>Scanner must supply a record from the verified resumed session's native transcript, never stdout.</summary>
    public bool ConfirmManagedInput(string questionId, string resumedJobId, string transcriptSessionId, string json)
    {
        var row = waits.Get(questionId);
        if (row is not { Status: "resumed", AnswerCommandId: { } command } || row.ResumedJobId != resumedJobId) { return false; }
        var job = jobs.GetJob(resumedJobId);
        var parent = jobs.GetJob(row.JobId!);
        if (job is null || parent?.SessionId is null || job.SessionId != parent.SessionId
            || transcriptSessionId != parent.SessionId || job.Backend != parent.Backend) { return false; }
        return HumanWaitInputReceipt.Matches(job.Backend, json, HumanWait.AnswerPrompt(row))
            && waits.ConfirmInput(questionId, command, resumedJobId);
    }

    /// <summary>Call from authenticated mailbox read/explicit acknowledgement, never enqueue ACK.</summary>
    public bool ConfirmExternalInput(string server, Guid workItemId, string member, string questionId, Guid commandId)
    {
        var row = waits.Refresh(questionId).Wait;
        return row is { JobId: null, Status: "answer_reserved" } && row.Server == server && row.WorkItemId == workItemId
            && row.Member == member && waits.ConfirmInput(questionId, commandId, null);
    }
}

/// <summary>Only named native user-input records are evidence; startup ACKs and echoed assistant output are not.</summary>
public static class HumanWaitInputReceipt
{
    public static bool Matches(string backend, string json, string prompt)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = Text(root, "type");
            var envelope = backend switch
            {
                "claude" or "claude-code" when type == "user" => "message",
                "codex" when type == "response_item" => "payload",
                "pi" when type == "message" => "message",
                _ => null
            };
            if (envelope is null || !root.TryGetProperty(envelope, out var message)
                || Text(message, "role") != "user" || !message.TryGetProperty("content", out var content)) { return false; }
            return ContainsPrompt(content, prompt);
        }
        catch (JsonException) { return false; }
    }

    static string? Text(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object
        && node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static bool ContainsPrompt(JsonElement content, string prompt)
    {
        if (content.ValueKind == JsonValueKind.String) { return content.GetString() == prompt; }
        if (content.ValueKind != JsonValueKind.Array) { return false; }
        foreach (var part in content.EnumerateArray())
        {
            if (Text(part, "type") is "text" or "input_text" && Text(part, "text") == prompt) { return true; }
        }
        return false;
    }
}
