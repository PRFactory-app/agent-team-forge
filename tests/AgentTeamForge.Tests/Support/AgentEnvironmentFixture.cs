using System.Collections;

[assembly: Xunit.AssemblyFixture(typeof(AgentTeamForge.Tests.Support.AgentEnvironmentFixture))]

namespace AgentTeamForge.Tests.Support;

/// <summary>Removes inherited agent identity and wake state before any test runs.</summary>
public sealed class AgentEnvironmentFixture
{
    public AgentEnvironmentFixture()
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && ShouldClear(name))
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    internal static bool ShouldClear(string name)
    {
        if (name.StartsWith("ATF_", StringComparison.Ordinal))
        {
            return !IsTestControl(name);
        }
        if (name == "CODEX_THREAD_ID")
        {
            return true;
        }
        if (!name.StartsWith("CLAUDE_", StringComparison.Ordinal))
        {
            return false;
        }

        return name.Contains("SESSION", StringComparison.Ordinal)
            || name.Contains("MESSAGING", StringComparison.Ordinal)
            || name is "CLAUDE_PID" or "CLAUDE_CODE_SSE_PORT";
    }

    static bool IsTestControl(string name) =>
        name.StartsWith("ATF_TEST_", StringComparison.Ordinal)
        || name.StartsWith("ATF_REAL_", StringComparison.Ordinal)
        || name.StartsWith("ATF_LIVE_", StringComparison.Ordinal)
        || name.StartsWith("ATF_HERDR_", StringComparison.Ordinal)
        || name.StartsWith("ATF_CLAUDE_PREFLIGHT_", StringComparison.Ordinal)
        || name is "ATF_KEEP_TMP" or "ATF_HOST_BINARY" or "ATF_RELEASES_URL" or "ATF_FIXTURE" or "ATF_STOP_FAIL";
}
