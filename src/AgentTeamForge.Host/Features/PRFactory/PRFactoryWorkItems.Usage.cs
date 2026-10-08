using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Business.Features.Usage;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Host.Features.PRFactory;

public sealed partial class PRFactoryWorkItems
{
    /// <summary>
    /// Usage report for a work item, summed over the distinct sessions its member jobs ran (follow-up turns share
    /// a session and count once). A figure any session cannot report is omitted, never sent as zero. A team that
    /// mixed backends reports the lead's backend and model. No member job means no agent ran: no report.
    /// An external (joined) member's session is not readable here, so its presence makes every figure unknown.
    /// </summary>
    internal static PRFactoryUsageReport? UsageFor(PRFactoryWorkItem item, JobRecord? lead, IEnumerable<JobRecord> jobs,
        Func<JobRecord, TokenUsage?> read, bool hasExternalMembers = false)
    {
        var members = jobs.ToList();
        lead ??= members.FirstOrDefault();
        if (lead is null) { return null; }
        var backend = AgentType(lead.Backend);
        var model = JobOptions.Read(lead.Options, "model") ?? item.Model;
        if (hasExternalMembers) { return new(backend, model, null, null, null, null); }
        TokenUsage? total = new(0, 0, 0, 0);
        foreach (var session in members.DistinctBy(j => (AgentType(j.Backend), j.SessionId)))
        {
            var usage = session.SessionId is null ? null : read(session);
            if (usage is null) { total = null; break; }
            total += usage;
        }
        return new(backend, model, total?.Input, total?.Output, total?.CacheRead, total?.CacheWrite);
    }

    static PRFactoryAgentType? AgentType(string backend) => backend switch
    {
        "claude" or "claude-code" => PRFactoryAgentType.ClaudeCode,
        "codex" => PRFactoryAgentType.Codex,
        "pi" => PRFactoryAgentType.PiAgent,
        "cursor" => PRFactoryAgentType.CursorCli,
        "droid" => PRFactoryAgentType.Droid,
        _ => null,
    };
}
