using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

sealed record HerdrTeardownDecision(string? Problem, bool Stop);

/// <summary>
/// Ownership rules for a daemon-created Herdr session. A session is ours only if we created it under
/// a fresh random name that was absent (running or stopped), recorded the server's PID + start time,
/// and a workspace carrying our random owner label still exists in that running server. A stopped
/// session offers no such proof and is never stopped or deleted automatically.
/// </summary>
static class HerdrOwnership
{
    public static string NewSessionName(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));

    public static string NewOwnerLabel() => "atf-owner-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    /// <summary>Creation is allowed only for a name absent from Herdr's session list (running or stopped).</summary>
    public static bool CanCreate(JsonNode sessionList, string name) => Find(sessionList, name) is null;

    public static HerdrTeardownDecision Teardown(string? ownerLabel, string recordedSession, JsonNode sessionList, bool serverIdentityMatches, JsonNode? workspaces)
    {
        if (string.IsNullOrEmpty(ownerLabel))
        {
            return new("no ownership record: session was not created by this daemon", false);
        }
        if (Find(sessionList, recordedSession) is not { } session)
        {
            return new("recorded session no longer exists", false);
        }
        if (session["running"] is not JsonValue r || !r.TryGetValue<bool>(out var running) || !running)
        {
            // The name alone may now belong to a replacement; nothing proves it is still ours.
            return new("recorded session is not running; its ownership cannot be proven, so it is left for manual handling", false);
        }
        if (!serverIdentityMatches)
        {
            return new("running server is not the recorded process (PID + start time)", false);
        }
        return HasLabel(workspaces, ownerLabel) ? new(null, true) : new("owner label workspace missing from the running server", false);
    }

    public static bool HasLabel(JsonNode? workspaces, string ownerLabel) =>
        workspaces?["result"]?["workspaces"] is JsonArray ws &&
        ws.OfType<JsonObject>().Any(w => w["label"] is JsonValue l && l.TryGetValue<string>(out var s) && s == ownerLabel);

    public static JsonObject? Find(JsonNode sessionList, string name) =>
        (sessionList["sessions"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(s => s["name"] is JsonValue n && n.TryGetValue<string>(out var v) && v == name);
}
