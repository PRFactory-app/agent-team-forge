namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>Encodes arguments for Windows Terminal's parser and child command-line rebuild.</summary>
internal static class WtCommandLine
{
    // Windows Terminal splits every argv element at an unescaped semicolon, then
    // removes the backslash immediately before each escaped semicolon.
    internal static string EscapeDelimiter(string value) => value.Replace(";", @"\;", StringComparison.Ordinal);

    internal static string ChildArgument(string value)
    {
        // WT joins child tokens with spaces and only supplies outer quotes when
        // the token contains a literal space. Pre-encode the remaining MSVC argv
        // quoting so its rebuilt command line parses back to the original value.
        var encoded = WtTabControl.CommandLine([value]);
        if (value.Contains(' '))
        {
            encoded = encoded[1..^1];
        }
        return EscapeDelimiter(encoded);
    }

    // ExpandEnvironmentStringsW runs on the entire rebuilt child command line.
    // A pair can span arguments, and WT may use an existing window's environment.
    internal static bool MayExpand(IEnumerable<string> child) => child.Sum(value => value.Count(c => c == '%')) >= 2;

    /// <summary>Returns null when WT could expand a child value; the caller uses a console.</summary>
    internal static IReadOnlyList<string>? Arguments(IEnumerable<string> options, IReadOnlyList<string> child)
    {
        if (MayExpand(child)) { return null; }
        return [.. options.Select(EscapeDelimiter), "--", .. child.Select(ChildArgument)];
    }
}
