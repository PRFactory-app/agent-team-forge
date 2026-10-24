using System.Text.RegularExpressions;

namespace AgentTeamForge.Business.Features.Jobs;

/// <summary>Display remote identities without credentials or changes to their spelling.</summary>
public static partial class RemoteDisplay
{
    public static string Redact(string? remote) => remote is null ? "(unset)" : UserInfo().Replace(remote, "$1://");

    [GeneratedRegex(@"([A-Za-z][A-Za-z0-9+.-]*)://[^/?#]*@")]
    private static partial Regex UserInfo();
}
