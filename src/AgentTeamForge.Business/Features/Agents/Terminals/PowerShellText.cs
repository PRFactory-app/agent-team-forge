namespace AgentTeamForge.Business.Features.Agents.Terminals;

public static class PowerShellText
{
    /// <summary>
    /// A PowerShell single-quoted literal. PowerShell also ends such a literal at the
    /// typographic quotes U+2018..U+201B, so each of those is doubled as well.
    /// </summary>
    public static string Quote(string value)
    {
        var quoted = new System.Text.StringBuilder("'");
        foreach (var c in value)
        {
            quoted.Append(c);
            if (c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B')
            {
                quoted.Append(c);
            }
        }
        return quoted.Append('\'').ToString();
    }
}
