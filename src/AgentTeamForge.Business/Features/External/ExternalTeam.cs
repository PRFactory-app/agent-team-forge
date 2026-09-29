using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.DAL.Features.External;
using AgentTeamForge.DAL.Features.Wake;

namespace AgentTeamForge.Business.Features.External;

public sealed record ExternalResult(string? Error = null, JoinTicket? Ticket = null, JoinedMember? Member = null,
    ExternalInbox? Inbox = null, long? WakeGeneration = null, bool? AlreadyLeft = null, string? Name = null,
    string? ErrorDetail = null)
{
    public bool Ok => Error is null;
}

public sealed class ExternalTeam(ExternalMemberStore members, WakeStore wake, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    static readonly Regex SafeName = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);
    static readonly Regex Token = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    const int MaxText = 65536;
    static readonly TimeSpan TicketTtl = TimeSpan.FromMinutes(10);
    string? MemberSecret(string? token)
    {
        if (token is null)
        {
            return null;
        }

        if (Token.IsMatch(token))
        {
            return token;
        }

        var parts = token.Split(':');
        return parts.Length == 3 && parts[0] == "wam1"
            && Guid.TryParseExact(parts[1], "D", out var session) && session.ToString("D") == parts[1]
            && Token.IsMatch(parts[2]) && members.TokenBelongsToTeam(parts[2], parts[1]) ? parts[2] : null;
    }

    /// <summary>Authenticates a member token to its live member name (managed children: child-&lt;root job&gt;).</summary>
    public string? MemberName(string? token) => MemberSecret(token) is { } secret ? members.ActiveMemberName(secret) : null;

    public string? ManagedChildName(string? token) => MemberSecret(token) is { } secret ? members.ManagedChildName(secret) : null;

    /// <summary>The connector calls this with a stable work-item key; retries recover the same team ID.</summary>
    public string? CreateActorTeam(string ownerKey) => ownerKey is { Length: > 0 and <= 256 }
        ? members.CreateActorTeam(ownerKey, now()) : null;

    /// <summary>Shared ticket path for MCP leads and in-daemon actors.</summary>
    public ExternalResult CreateTicketForTeam(string? teamId, string? name, string? note)
    {
        if (teamId is null || name is null || !SafeName.IsMatch(name) || note is { Length: > 4096 })
        {
            return new("invalid_request");
        }

        var ticket = members.CreateTicket(teamId, name, note ?? "", now(), TicketTtl);
        return ticket is null ? new("invalid_team_or_name") : new(Ticket: ticket);
    }

    /// <summary>Re-issue a ticket nobody used before it expired; null while it is valid, used or revoked.</summary>
    public JoinTicket? RenewExpiredTicket(string teamId, string name) =>
        members.RenewExpiredTicket(teamId, name, now(), TicketTtl);

    public bool HasLeft(string teamId, string name) => members.HasLeft(teamId, name);

    public ExternalResult CreateTicket(string? sessionId, string? workspace, string? name, string? note)
    {
        if (sessionId is null || workspace is null || name is null || !SafeName.IsMatch(name) || note is { Length: > 4096 })
        {
            return new("invalid_request");
        }

        if (!members.EnsureMcpTeam(sessionId, workspace, now()))
        {
            return new("invalid_session");
        }
        return CreateTicketForTeam(sessionId, name, note);
    }

    public ExternalResult Join(string? sessionId, string? ticket)
    {
        if (sessionId is null)
        {
            return new("invalid_request");
        }

        if (ticket is null || ticket.Length != 64)
        {
            return new("invalid_or_expired_token");
        }

        var (member, left) = members.Join(sessionId, ticket, now());
        return left ? new("membership_revoked") : member is null
            ? new("invalid_or_expired_token")
            : new(Member: member with { MemberToken = $"wam1:{sessionId}:{member.MemberToken}" });
    }

    public ExternalResult Send(string? token, string? text)
    {
        var secret = MemberSecret(token);
        if (secret is null || text is null || text.Length is < 1 or > MaxText)
        {
            return new("invalid_request");
        }

        return members.SendFromMember(secret, text, now()) ? new() : new("membership_revoked");
    }

    public ExternalResult SendFromLead(string? sessionId, string? workspace, string? name, string? text)
    {
        if (sessionId is null || workspace is null || !members.EnsureMcpTeam(sessionId, workspace, now()))
        {
            return new("invalid_session", ErrorDetail: "No active AgentTeamForge lead session is available for send_message.");
        }
        var result = SendToMember(sessionId, name, text);
        if (result.Error != "member_not_found")
        {
            return result;
        }

        var valid = members.ActiveMemberNames(sessionId);
        return result with { ErrorDetail = $"Unknown recipient '{name}'. Valid recipients in this AgentTeamForge lead session: {(valid.Count == 0 ? "none (no external members have joined)" : string.Join(", ", valid))}." };
    }

    /// <summary>Send from a trusted daemon actor to a joined member; the wake scan sees only committed rows.</summary>
    public ExternalResult SendToMember(string? teamId, string? name, string? text, string sender = "team-lead")
        => SendToMemberOnce(teamId, name, text, sender, null);

    /// <summary>Atomically deduplicate a server command with the inbox insertion.</summary>
    public ExternalResult SendToMemberOnce(string? teamId, string? name, string? text, string sender, string? commandId)
    {
        if (teamId is null || name is null || !SafeName.IsMatch(name) || text is null || text.Length is < 1 or > MaxText
            || string.IsNullOrWhiteSpace(sender) || sender.Length > 64)
        {
            return new("invalid_request");
        }

        return members.SendToMember(teamId, name, text, sender, now(), commandId) ? new() : new("member_not_found");
    }

    public ExternalResult Read(string? token, long? sinceSeq, int? limit, string? fromAgent = null, bool full = false, int? maxChars = null)
    {
        var secret = MemberSecret(token);
        if (secret is null || sinceSeq is < 0 || limit is < 0 or > 10000 || maxChars is < 0 or > 65536
            || fromAgent is { Length: > 64 } || sinceSeq is not null && string.IsNullOrEmpty(fromAgent))
        {
            return new("invalid_request");
        }

        var inbox = members.ReadMemberCompat(secret, sinceSeq, full ? int.MaxValue - 1 : limit ?? 50, now(),
            string.IsNullOrEmpty(fromAgent) ? null : fromAgent, maxChars);
        if (inbox is null)
        {
            return new("membership_revoked");
        }
        return new(Inbox: inbox);
    }

    public ExternalResult ReadLead(string? sessionId, string? workspace, long? sinceSeq, int? limit,
        string? fromAgent = null, bool full = false, int? maxChars = null)
    {
        if (sessionId is null || workspace is null || !members.EnsureMcpTeam(sessionId, workspace, now()))
        {
            return new("invalid_session");
        }
        if (sinceSeq is < 0) { return new("invalid_request", ErrorDetail: "Invalid since_seq: must be nonnegative."); }
        if (limit is < 0 or > 10000) { return new("invalid_request", ErrorDetail: "Invalid limit: must be between 0 and 10000."); }
        if (maxChars is < 0 or > 65536) { return new("invalid_request", ErrorDetail: "Invalid max_chars: must be between 0 and 65536."); }
        if (fromAgent is { Length: > 64 }) { return new("invalid_request", ErrorDetail: "Invalid from_agent: must be at most 64 characters."); }

        var inbox = members.ReadLeadCompat(sessionId, sinceSeq, full ? int.MaxValue - 1 : limit ?? 50, now(),
            string.IsNullOrEmpty(fromAgent) ? null : fromAgent, maxChars);
        if (inbox is null)
        {
            return new("invalid_team");
        }

        return new(Inbox: inbox);
    }

    /// <summary>Read replies for the team owner, including a connector actor, using a durable cursor.</summary>
    public ExternalResult ReadTeam(string? teamId, long? sinceSeq, int? limit)
    {
        if (teamId is null || sinceSeq is < 0 || limit is < 0 or > 10000)
        {
            return new("invalid_request");
        }

        var inbox = members.ReadTeam(teamId, sinceSeq, limit ?? 50, now());
        return inbox is null ? new("invalid_team") : new(Inbox: inbox);
    }

    public bool BindTeamWake(string teamId, string wakeKey, long generation) =>
        members.BindTeamWake(teamId, wakeKey, generation);

    public bool CloseTeam(string teamId) => members.CloseTeam(teamId, now());

    public bool RevokeMember(string teamId, string name) => members.RevokeMember(teamId, name, now());

    public ExternalResult Leave(string? token)
    {
        var secret = MemberSecret(token);
        if (secret is null)
        {
            return new("invalid_request");
        }

        return members.Leave(secret, now()) switch
        {
            (1, var name) => new(AlreadyLeft: false, Name: name),
            (2, var name) => new(AlreadyLeft: true, Name: name),
            _ => new("membership_revoked")
        };
    }

    public ExternalResult SetClaudeWake(string? token, string? address, string? channelSecret, string? hostPid)
    {
        var secret = MemberSecret(token);
        if (secret is null) { return new("invalid_request"); }
        if (!members.IsActive(secret)) { return new("membership_revoked"); }
        if (!ClaudeChannel.Valid(address, channelSecret, hostPid)) { return new("invalid_claude_wake"); }
        var key = "external:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
        var registration = wake.Register(key, "claude", address!, channelSecret!, hostPid!);
        return members.SetMemberWake(secret, key) ? new(WakeGeneration: registration.Generation) : new("membership_revoked");
    }

    public ExternalResult SetWake(string? token, string? threadId, string? home)
    {
        var secret = MemberSecret(token);
        if (secret is null || threadId is null)
        {
            return new("invalid_request");
        }

        if (!members.IsActive(secret))
        {
            return new("membership_revoked");
        }

        if (threadId.Length == 0)
        {
            var clearKey = "external:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
            if (!members.SetMemberWake(secret, null)) { return new("membership_revoked"); }
            wake.Invalidate(clearKey);
            return new();
        }

        if (!Guid.TryParseExact(threadId, "D", out var parsed) || parsed.ToString("D") != threadId
            || home is null || !Path.IsPathFullyQualified(home) || home.Length > 4096)
        {
            return new("invalid_codex_wake");
        }

        var key = "external:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
        var target = new WakeRegistration(key, 0, "codex", threadId, "", home);
        if (!CodexQueueWake.VerifyCodexThread(target))
        {
            return new("invalid_codex_wake");
        }

        var registration = wake.Register(key, "codex", threadId, "", home);
        return members.SetMemberWake(secret, key) ? new(WakeGeneration: registration.Generation) : new("membership_revoked");
    }
}
