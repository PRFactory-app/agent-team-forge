using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentTeamForge.Business.Features.Wake;
using AgentTeamForge.DAL.Features.External;
using AgentTeamForge.DAL.Features.Wake;

namespace AgentTeamForge.Business.Features.External;

public sealed record ExternalResult(string? Error = null, JoinTicket? Ticket = null, JoinedMember? Member = null,
    ExternalInbox? Inbox = null, long? WakeGeneration = null)
{
    public bool Ok => Error is null;
}

public sealed class ExternalTeam(ExternalMemberStore members, WakeStore wake, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    static readonly Regex SafeName = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);
    static readonly Regex Token = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    const int MaxText = 65536;
    static bool ValidToken(string? token) => token is not null && Token.IsMatch(token);

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

        var ticket = members.CreateTicket(teamId, name, note ?? "", now(), TimeSpan.FromMinutes(10));
        return ticket is null ? new("invalid_team_or_name") : new(Ticket: ticket);
    }

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
        if (sessionId is null || ticket is null || ticket.Length != 64)
        {
            return new("invalid_request");
        }

        var member = members.Join(sessionId, ticket, now());
        return member is null ? new("invalid_or_expired_ticket") : new(Member: member);
    }

    public ExternalResult Send(string? token, string? text)
    {
        if (!ValidToken(token) || text is null || text.Length is < 1 or > MaxText)
        {
            return new("invalid_request");
        }

        return members.SendFromMember(token!, text, now()) ? new() : new("membership_revoked");
    }

    public ExternalResult SendFromLead(string? sessionId, string? workspace, string? name, string? text)
    {
        if (sessionId is null || workspace is null || !members.EnsureMcpTeam(sessionId, workspace, now()))
        {
            return new("invalid_session");
        }
        return SendToMember(sessionId, name, text);
    }

    /// <summary>Send from a trusted daemon actor to a joined member; the wake scan sees only committed rows.</summary>
    public ExternalResult SendToMember(string? teamId, string? name, string? text, string sender = "team-lead")
    {
        if (teamId is null || name is null || !SafeName.IsMatch(name) || text is null || text.Length is < 1 or > MaxText
            || string.IsNullOrWhiteSpace(sender) || sender.Length > 64)
        {
            return new("invalid_request");
        }

        return members.SendToMember(teamId, name, text, sender, now()) ? new() : new("member_not_found");
    }

    public ExternalResult Read(string? token, long? sinceSeq, int? limit, string? fromAgent = null, bool full = false, int? maxChars = null)
    {
        if (!ValidToken(token) || sinceSeq is < 0 || limit is < 0 or > 10000 || maxChars is < 0 or > 65536
            || fromAgent is { Length: > 64 })
        {
            return new("invalid_request");
        }

        var inbox = members.ReadMember(token!, sinceSeq, full ? int.MaxValue - 1 : limit ?? 50, now(),
            string.IsNullOrEmpty(fromAgent) ? null : fromAgent);
        if (inbox is null)
        {
            return new("membership_revoked");
        }
        if (maxChars is { } max)
        {
            inbox = inbox with
            {
                Messages = [.. inbox.Messages.Select(message => message.Text.Length > max
                ? message with { Text = message.Text[..max], Truncated = true, FullLen = message.Text.Length }
                : message)]
            };
        }
        return new(Inbox: inbox);
    }

    public ExternalResult ReadLead(string? sessionId, string? workspace, long? sinceSeq, int? limit)
    {
        if (sessionId is null || workspace is null || !members.EnsureMcpTeam(sessionId, workspace, now()))
        {
            return new("invalid_session");
        }
        return ReadTeam(sessionId, sinceSeq, limit);
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

    public ExternalResult Leave(string? token)
    {
        if (!ValidToken(token))
        {
            return new("invalid_request");
        }

        return members.Leave(token!, now()) ? new() : new("membership_revoked");
    }

    public ExternalResult SetWake(string? token, string? threadId, string? home)
    {
        if (!ValidToken(token) || threadId is null)
        {
            return new("invalid_request");
        }

        if (!members.IsActive(token!))
        {
            return new("membership_revoked");
        }

        if (threadId.Length == 0)
        {
            return members.SetMemberWake(token!, null) ? new() : new("membership_revoked");
        }

        if (!Guid.TryParseExact(threadId, "D", out var parsed) || parsed.ToString("D") != threadId
            || home is null || !Path.IsPathFullyQualified(home) || home.Length > 4096)
        {
            return new("invalid_codex_wake");
        }

        var key = "external:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token!))).ToLowerInvariant();
        var target = new WakeRegistration(key, 0, "codex", threadId, "", home);
        if (!CodexQueueWake.VerifyCodexThread(target))
        {
            return new("invalid_codex_wake");
        }

        var registration = wake.Register(key, "codex", threadId, "", home);
        return members.SetMemberWake(token!, key) ? new(WakeGeneration: registration.Generation) : new("membership_revoked");
    }
}
