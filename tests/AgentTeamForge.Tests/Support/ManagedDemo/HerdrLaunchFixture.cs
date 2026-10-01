using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentTeamForge.Business.Features.Agents.Terminals;

namespace AgentTeamForge.Tests.Support.ManagedDemo;

/// <summary>
/// D2 characterization surface, now delegating to the production Herdr launch rules promoted by D7
/// (<c>src/AgentTeamForge.Business/Features/Agents/Terminals</c>), so the reviewed vectors pin
/// production code. Provenance of the original rules: docs/spikes/char-d2-report.md (git history). Nothing here
/// starts a process.
/// </summary>
public static class HerdrLaunchFixture
{
    public const string ServerScript = HerdrCommands.ServerScript;

    public const string MacServerScript = HerdrCommands.MacServerScript;

    public static bool IsInheritedSessionContext(string name) => LaunchEnvironment.IsInheritedSessionContext(name);

    public static void ApplyEnvironment(IDictionary<string, string?> environment, string? extraAllowed) => LaunchEnvironment.Apply(environment, extraAllowed);

    /// <summary><paramref name="socketPath"/> null is a global (session-management) command.</summary>
    public static ProcessStartInfo CommandStartInfo(IReadOnlyDictionary<string, string?> seed, string? extraAllowed, string? socketPath, params string[] args) =>
        HerdrCommands.CommandStartInfo(seed, extraAllowed, socketPath, args);

    public static ProcessStartInfo OwnedServerStartInfo(JsonNode sessionList, string sessionName, IReadOnlyDictionary<string, string?> seed, string? extraAllowed) =>
        HerdrCommands.OwnedServerStartInfo(sessionList, sessionName, seed, extraAllowed);

    public static bool CanCreate(JsonNode sessionList, string name) => HerdrOwnership.CanCreate(sessionList, name);

    public static TeardownDecision Teardown(OwnedSession? owned, string recordedSession, JsonNode sessionList, bool serverIdentityMatches, JsonNode? workspaces)
    {
        var d = HerdrOwnership.Teardown(owned?.OwnerLabel, recordedSession, sessionList, serverIdentityMatches, workspaces);
        return new(d.Problem, d.Stop);
    }
}

public sealed record TeardownDecision(string? Problem, bool Stop);

public sealed record OwnedSession(string OwnerLabel, int ServerPid, long ServerStartTicks);
