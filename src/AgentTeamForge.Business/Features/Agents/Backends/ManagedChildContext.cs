using System.Text.Json.Nodes;
using AgentTeamForge.Business.Features.External;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Files;

namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>Private launch configuration, backed by the existing external membership and lead relation.</summary>
public sealed class ManagedChildContext(JobStore jobs, ExternalTeam teams, string stateRoot, string binary)
{
    public const string ServerName = "agentteamforge";
    public const string ToolPrefix = "mcp__agentteamforge__";

    public BackendRequest Prepare(BackendRequest request)
    {
        var root = jobs.GetJob(request.JobId)!;
        while (root.ParentJobId is { } parent) { root = jobs.GetJob(parent)!; }
        // These CLIs read MCP from user/project files and have no per-run config flag.
        // Do not write into the checkout or the operator's CLI configuration.
        if (root.Backend is BackendCatalog.Cursor or BackendCatalog.Droid) { return request; }
        if (jobs.LeadForJob(root.JobId) is not { } lead) { return request; }
        var directory = Path.Combine(stateRoot, "managed-children", root.JobId);
        PrivateFiles.CreateDirectory(directory);
        var contextPath = Path.Combine(directory, "context.json");
        // Neither local jobs nor the current PRFactory wire can answer a managed child's question.
        const bool humanInputAvailable = false;
        if (!File.Exists(contextPath))
        {
            var ticket = teams.CreateTicket(lead.SessionId, lead.Workspace, "child-" + root.JobId, "Managed agent").Ticket
                ?? throw new InvalidOperationException("managed child parent unavailable");
            var member = teams.Join(ticket.SessionId, ticket.Token).Member
                ?? throw new InvalidOperationException("managed child membership unavailable");
            Write(contextPath, new JsonObject
            {
                ["state_dir"] = stateRoot,
                ["parent_session_id"] = lead.SessionId,
                ["member_token"] = member.MemberToken,
                ["binding_key"] = "managed-child:" + root.JobId,
                ["human_input_available"] = humanInputAvailable,
            });
        }
        else
        {
            // Existing managed sessions keep their token, but gain the current capability gate.
            var existing = JsonNode.Parse(File.ReadAllText(contextPath))!.AsObject();
            existing["human_input_available"] = humanInputAvailable;
            Write(contextPath, existing);
        }
        // Every follow-up points to the original membership and nested-lead binding.
        var configPath = ConfigPath(stateRoot, request.JobId);
        PrivateFiles.CreateDirectory(Path.GetDirectoryName(configPath)!);
        Write(configPath, new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ServerName] = new JsonObject
                {
                    ["command"] = binary,
                    ["args"] = new JsonArray("mcp", "--state-dir", stateRoot, "--managed-context", contextPath),
                },
            },
        });
        // A PRFactory connector "lead" is a synthetic session nobody reads; completion is native, so no report step.
        var connectorLead = lead.BindingKey.StartsWith("prfactory:", StringComparison.Ordinal);
        var hint = connectorLead ? "AgentTeamForge routing: " : $"AgentTeamForge routing: report to your parent with {ToolPrefix}send_message(to=\"team-lead\", text=...). ";
        hint += $"Use {ToolPrefix} tools for ATF children; win-agent-teams and Codex built-in collaboration address different teams. "
            + $"Your nested lead is separate from your parent membership. {ToolPrefix}read_messages reads your children's reports. "
            + $"New managed work arrives through {ToolPrefix}follow_up or send_message(job_id=..., idempotency_key=...); a live Codex turn queues new work behind the current turn. "
            + "Messages addressed to external members are inbox-only. "
            + (connectorLead ? "Finish your turn when the work is done; do not poll. " : "Report DONE/FAILED, commit and tests when relevant, then finish your turn; do not poll. ")
            + "Final output completes the job only with native backend completion; the word DONE is not a scheduler signal.";
        if (root.Backend == "pi")
        {
            hint = hint.Replace(ToolPrefix, "agentteamforge_", StringComparison.Ordinal)
                + (connectorLead ? " In Pi use the MCP adapter proxy: mcp({tool:\"agentteamforge_<tool>\",args:{...}}); ATF tools use the agentteamforge_ prefix."
                    : " In Pi use the MCP adapter proxy: mcp({tool:\"agentteamforge_send_message\",args:{to:\"team-lead\",text:\"your report\"}}); other ATF tools use the same agentteamforge_ prefix.");
        }
        return request with { ManagedMcpConfig = configPath, Instruction = root.Backend == "fake" ? request.Instruction : request.Instruction + "\n\n" + hint };
    }

    public static string ConfigPath(string stateRoot, string jobId) => Path.Combine(stateRoot, "managed-children", jobId, "mcp.json");

    public static void ClearInheritedIdentity(System.Diagnostics.ProcessStartInfo info)
    {
        foreach (var key in info.Environment.Keys.ToArray())
        {
            if (Terminals.LaunchEnvironment.IsInheritedSessionContext(key)) { info.Environment.Remove(key); }
        }
    }

    public static IReadOnlyList<string> Arguments(string backend, string? configPath)
    {
        if (configPath is null || !File.Exists(configPath)) { return []; }
        if (backend != "codex") { return ["--mcp-config", configPath]; }
        var server = JsonNode.Parse(File.ReadAllText(configPath))!["mcpServers"]![ServerName]!;
        return ["-c", $"mcp_servers.{ServerName}.command=" + server["command"]!.ToJsonString(),
            "-c", $"mcp_servers.{ServerName}.args=" + server["args"]!.ToJsonString()];
    }

    static void Write(string path, JsonObject value)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, PrivateFiles.Options(FileMode.Create, FileAccess.Write)))
        using (var writer = new StreamWriter(stream)) { writer.Write(value.ToJsonString()); }
        File.Move(temp, path, overwrite: true);
    }
}
