using System.Text.Json.Serialization;

namespace AgentTeamForge.Host.Features.PRFactory;

// Wire names and enum strings mirror PRFactory.Worker/Api/Models/WorkerWorkItemDto.cs.
[JsonConverter(typeof(JsonStringEnumConverter<PRFactoryAgentType>))]
public enum PRFactoryAgentType { ClaudeCode, Codex, CursorCli, PiAgent, Droid }
[JsonConverter(typeof(JsonStringEnumConverter<PRFactoryEffort>))]
public enum PRFactoryEffort { Low, Medium, High, XHigh, Max, Ultra }
public enum PRFactoryWorkItemType
{
    TicketRefinement, Planning, TestPlan, Implementation, CodeReview, Discovery, VisualQa,
    PlanReview, ClarifyingQuestions, Decomposition, HostingNeedsDerivation, HostingResearch, CustomStep, PullRequestCreate
}

public sealed class PRFactoryWorkItem
{
    public Guid Id { get; set; }
    public string? Type
    {
        get;
        // Worker enum names are camel-cased on the server wire; older fixtures use PascalCase.
        set => field = Enum.TryParse<PRFactoryWorkItemType>(value, ignoreCase: true, out var type) ? type.ToString() : value;
    }
    public Guid TicketId { get; set; }
    public string? TicketKey { get; set; }
    public string? TicketSource { get; set; }
    public string? StepKey { get; set; }
    public string? StartFromBranch { get; set; }
    public string? StartCommitSha { get; set; }
    public string? HandoverReleaseId { get; set; }
    public Guid? HandoverRepositoryId { get; set; }
    public string? HandoverBaseCommitSha { get; set; }
    public string? Disposition { get; set; }
    public string? DispositionReason { get; set; }
    public PRFactoryWorkspaceRevision? Continuation { get; set; }
    public PRFactoryWorkspaceRevision? BaseSnapshot { get; set; }
    // base-wip-v1: approved plan basis for this repository; a moved base requires server checkpoint.
    public string? PlanBasisCommitSha { get; set; }
    public string? PublishBranch { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    // Repository set/default base are carried in ContextJson by the worker contract.
    // Retain repository outcomes (including base SHA/branch) for publication slices.
    public System.Text.Json.JsonElement? RepositoryResults { get; set; }
    public Guid? RepositoryId { get; set; }
    public int AttemptCount { get; set; }
    public PRFactoryAgentType AgentType { get; set; }
    public string? Model { get; set; }
    public PRFactoryEffort? Effort { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string? ContextJson { get; set; }
    public string TicketArtefactFolder { get; set; } = string.Empty;
    public string? ExpectedOutput { get; set; }
    public bool ReadOnly { get; set; }
    public Guid? LeaseToken { get; set; }
    public PRFactoryTeamPlan? TeamPlan { get; set; }
}

public sealed record PRFactoryWorkspaceRevision(string Branch, string CommitSha);

public sealed class PRFactoryTeamPlan
{
    public string RecipeName { get; set; } = string.Empty;
    public int RecipeVersion { get; set; }
    public int FreeRoomCeiling { get; set; }
    public int MaxConcurrentChildren { get; set; }
    public List<PRFactoryTeamMember> Members { get; set; } = [];
}

public sealed class PRFactoryTeamMember
{
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsLead { get; set; }
    public PRFactoryAgentType? Backend { get; set; }
    public string? Model { get; set; }
    public PRFactoryEffort? Effort { get; set; }
    public int? MaxIterations { get; set; }
    public int Order { get; set; }
    public string? Notes { get; set; }
}

// Limit fields are absent on servers that predate the worker cap advertisement.
public sealed record PRFactoryPollResponse(List<PRFactoryWorkItem> WorkItems, int? MaxConcurrentWorkItems = null, int? ActiveWorkItems = null);
public sealed record PRFactoryServerLimit(int Max, int Active, DateTimeOffset At)
{
    public bool AtCap => Active >= Max;
}
public sealed record PRFactoryClaimResponse(PRFactoryWorkItem? WorkItem);
public sealed record PRFactoryClaimRequest(string MachineName, string WorkerVersion, Guid? MachineId);
public sealed record PRFactoryAtfAcceptRequest(Guid MachineId, Guid LeaseToken, string JobId);
public sealed record PRFactoryAtfAcceptanceResponse(string? AtfJobId, System.Text.Json.JsonElement Status,
    string? Disposition = null, string? DispositionReason = null, string? HandoverReleaseId = null,
    Guid? HandoverRepositoryId = null, string? HandoverBaseCommitSha = null,
    string? StartFromBranch = null, string? StartCommitSha = null);
public sealed record PRFactoryLeaseHeartbeatRequest(Guid LeaseToken);
public sealed record PRFactoryArtefactFile(string FileName, string Content, string? Kind);
public sealed record PRFactoryArtefactRequest(List<PRFactoryArtefactFile> Artefacts, Guid? LeaseToken);
public sealed record PRFactoryCompletionRequest(bool Success, string? ResultMarkdown, string? ResultBranch, string? ResultCommitSha, string Metadata, Guid? LeaseToken,
    PRFactoryRemotePublication? Publication = null, List<PRFactoryRepositoryFreshnessRequest>? RepositoryResults = null);
// remote-publication-v1: the branch/head were pushed and verified with ls-remote; the server opens the PR remotely.
public sealed record PRFactoryRemotePublication(bool Remote, string Branch, string HeadSha, bool Verified);
public sealed record PRFactoryFailureRequest(string ErrorMessage, string ErrorDetails, bool ShouldRetry, string PartialResult, Guid? LeaseToken);
// pull-request-v1: contextJson of a PullRequestCreate item and the resultMarkdown that completes it.
public sealed record PRFactoryPullRequestRequest(string Kind, int Version, Guid SourceWorkItemId, Guid RepositoryId,
    string HeadBranch, string HeadSha, string BaseBranch, string Title, string Body);
public sealed record PRFactoryPullRequestResult(string Kind, int Version, int Number, string Url, string HeadSha, bool Created);
public sealed record PRFactoryCompletionResponse(bool Accepted);
public sealed record PRFactoryFailureResponse(bool Acknowledged);
// QuestionId correlates a human answer to request_human_input (human-wait-v1); absent for ordinary sends.
public sealed record PRFactoryCommand(Guid CommandId, string Kind, string TargetAgentName, string? Text, string? QuestionId = null);
public sealed record PRFactoryHumanWaitNotice(string QuestionId, string Question, string Status, Guid? AnswerCommandId, string? Error);
public sealed record PRFactoryCommandDrainResponse(List<PRFactoryCommand> Commands);
public sealed record PRFactoryCommandAck(Guid CommandId, bool Accepted, string? Reason);
public sealed record PRFactoryCommandAckRequest(Guid LeaseToken, List<PRFactoryCommandAck> Acks);
public sealed record PRFactoryCommandAckResponse(int Applied);
public sealed record PRFactoryStreamLine(string AgentName, long Seq, DateTimeOffset At, string Stream, string Text, string? RecordKind);
public sealed record PRFactoryStreamEvent(string Kind, string? TeamId, string AgentName, string Backend, string? State,
    Guid? RepositoryId, string? SpawnedBy);
public sealed record PRFactoryStreamBatch(Guid? LeaseToken, string BatchId, List<PRFactoryStreamEvent> Events, List<PRFactoryStreamLine> Lines);
public sealed record PRFactoryStreamResponse(bool Accepted, Dictionary<string, long> AcceptedThroughSeq, int CreditLines, int CreditBytes);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PRFactoryPollResponse))]
[JsonSerializable(typeof(PRFactoryServerLimit))]
[JsonSerializable(typeof(PRFactoryClaimResponse))]
[JsonSerializable(typeof(PRFactoryClaimRequest))]
[JsonSerializable(typeof(PRFactoryAtfAcceptRequest))]
[JsonSerializable(typeof(PRFactoryAtfAcceptanceResponse))]
[JsonSerializable(typeof(PRFactoryLeaseHeartbeatRequest))]
[JsonSerializable(typeof(PRFactoryArtefactRequest))]
[JsonSerializable(typeof(PRFactoryPlanBasis))]
[JsonSerializable(typeof(PRFactoryCompletionRequest))]
[JsonSerializable(typeof(PRFactoryFailureRequest))]
[JsonSerializable(typeof(PRFactoryPullRequestRequest))]
[JsonSerializable(typeof(PRFactoryPullRequestResult))]
[JsonSerializable(typeof(PRFactoryCompletionResponse))]
[JsonSerializable(typeof(PRFactoryFailureResponse))]
[JsonSerializable(typeof(PRFactoryWorkItem))]
[JsonSerializable(typeof(PRFactoryWorkspaceRevision))]
[JsonSerializable(typeof(PRFactoryCommandDrainResponse))]
[JsonSerializable(typeof(PRFactoryCommand))]
[JsonSerializable(typeof(PRFactoryCommandAckRequest))]
[JsonSerializable(typeof(PRFactoryCommandAckResponse))]
[JsonSerializable(typeof(PRFactoryStreamBatch))]
[JsonSerializable(typeof(PRFactoryStreamResponse))]
[JsonSerializable(typeof(PRFactoryHumanWaitNotice))]
internal sealed partial class PRFactoryWorkItemJson : JsonSerializerContext;
