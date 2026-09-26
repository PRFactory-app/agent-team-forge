using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentTeamForge.Host.Features.PRFactory;

// Wire names and enum strings mirror PRFactory.Worker/Api/Models/WorkerWorkItemDto.cs.
[JsonConverter(typeof(JsonStringEnumConverter<PRFactoryAgentType>))]
public enum PRFactoryAgentType { ClaudeCode, Codex, CursorCli, PiAgent, Droid }
[JsonConverter(typeof(JsonStringEnumConverter<PRFactoryEffort>))]
public enum PRFactoryEffort { Low, Medium, High, XHigh, Max, Ultra }

public sealed class PRFactoryWorkItem
{
    public Guid Id { get; set; }
    public Guid RepositoryId { get; set; }
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

public sealed record PRFactoryPollResponse(List<PRFactoryWorkItem> WorkItems);
public sealed record PRFactoryClaimResponse(PRFactoryWorkItem? WorkItem);
public sealed record PRFactoryClaimRequest(string MachineName, string WorkerVersion, Guid? MachineId);
public sealed record PRFactoryArtefactFile(string FileName, string Content, string? Kind);
public sealed record PRFactoryArtefactRequest(List<PRFactoryArtefactFile> Artefacts, Guid? LeaseToken);
public sealed record PRFactoryCompletionRequest(bool Success, string? ResultMarkdown, string? ResultBranch, string? ResultCommitSha, string Metadata, Guid? LeaseToken);
public sealed record PRFactoryFailureRequest(string ErrorMessage, string ErrorDetails, bool ShouldRetry, string PartialResult, Guid? LeaseToken);
public sealed record PRFactoryCompletionResponse(bool Accepted);
public sealed record PRFactoryFailureResponse(bool Acknowledged);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PRFactoryPollResponse))]
[JsonSerializable(typeof(PRFactoryClaimResponse))]
[JsonSerializable(typeof(PRFactoryClaimRequest))]
[JsonSerializable(typeof(PRFactoryArtefactRequest))]
[JsonSerializable(typeof(PRFactoryCompletionRequest))]
[JsonSerializable(typeof(PRFactoryFailureRequest))]
[JsonSerializable(typeof(PRFactoryCompletionResponse))]
[JsonSerializable(typeof(PRFactoryFailureResponse))]
[JsonSerializable(typeof(PRFactoryWorkItem))]
internal sealed partial class PRFactoryWorkItemJson : JsonSerializerContext;
