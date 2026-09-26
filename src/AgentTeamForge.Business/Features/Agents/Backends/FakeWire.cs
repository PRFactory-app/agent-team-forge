using System.Text.Json.Serialization;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>One JSON line on the fake child's stdin.</summary>
public sealed record FakeRequestLine(string JobId, string Correlation, string Instruction, string Behavior, bool Hold, string? ResumeSessionId = null);

/// <summary>One JSON line on the fake child's stdout: type is "ack", "session" (output = session id) or "result".</summary>
public sealed record FakeOutputLine(string Type, string Correlation, string? Output);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(FakeRequestLine))]
[JsonSerializable(typeof(FakeOutputLine))]
public sealed partial class FakeWireJson : JsonSerializerContext;
