using System.Text.Json.Serialization;

namespace AgentTeamForge.Business.Features.Wake;

public sealed record ClaudeAuth(string Type, string Token);
public sealed record ClaudeUser(string Type, ClaudeMessage Message);
public sealed record ClaudeMessage(string Role, string Content);
public sealed record PiDoorbell(long Generation, string Notice);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ClaudeAuth))]
[JsonSerializable(typeof(ClaudeUser))]
[JsonSerializable(typeof(PiDoorbell))]
public sealed partial class WakeJson : JsonSerializerContext;
