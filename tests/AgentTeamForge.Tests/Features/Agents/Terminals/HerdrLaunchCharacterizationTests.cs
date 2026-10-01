using System.Text.Json.Nodes;
using AgentTeamForge.Tests.Support.ManagedDemo;

namespace AgentTeamForge.Tests.Features.Agents.Terminals;

/// <summary>Launch vectors and refusal cases carried from the reviewed legacy Herdr rules for D7.</summary>
public class HerdrLaunchCharacterizationTests
{
    private const string Session = "atf-spike-0a1b2c3d4e5f";
    private const string Sentinel = "atf-credential-sentinel-7f3c";

    private static readonly Dictionary<string, string?> BaseEnv = new()
    {
        ["PATH"] = "/usr/bin",
        ["HOME"] = "/home/u",
        ["LANG"] = "C.UTF-8",
    };

    private static JsonObject Sessions(params (string Name, bool Running)[] s) =>
        new() { ["sessions"] = new JsonArray([.. s.Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["running"] = x.Running })]) };

    private static JsonObject Workspaces(params string[] labels) =>
        new() { ["result"] = new JsonObject { ["workspaces"] = new JsonArray([.. labels.Select(l => (JsonNode)new JsonObject { ["label"] = l })]) } };

    [Fact]
    public void ArgumentVector_PreservesUnicodeAndMetacharacters()
    {
        string[] args = ["pane", "send-text", "--text", "héllo 世界 🚀 $(id) `id` ${HOME} a;b|c&&d > /dev/null 'q' \"dq\" * \\ \n2nd", "--", "-x"];

        var psi = HerdrLaunchFixture.CommandStartInfo(BaseEnv, null, "/run/s.sock", args);

        Assert.Equal("herdr", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Empty(psi.Arguments);
        Assert.Equal(args, psi.ArgumentList);

        // The server launch uses sh only with a fixed script; a hostile name stays one argv element.
        var hostile = "x\"; rm -rf ~; echo \"é🚀";
        var server = HerdrLaunchFixture.OwnedServerStartInfo(Sessions(), hostile, BaseEnv, null);

        // macOS has no setsid(1): sh backgrounds the server in its own process group and exits, so launchd adopts it.
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("/bin/sh", server.FileName);
            Assert.Equal(["-c", HerdrLaunchFixture.MacServerScript, hostile], server.ArgumentList);
        }
        else
        {
            Assert.Equal("setsid", server.FileName);
            Assert.Equal(["-f", "sh", "-c", HerdrLaunchFixture.ServerScript, hostile], server.ArgumentList);
        }
        Assert.DoesNotContain(hostile, HerdrLaunchFixture.ServerScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Environment_ExcludesCredentialSentinel()
    {
        var seed = new Dictionary<string, string?>(BaseEnv)
        {
            ["OPENAI_API_KEY"] = Sentinel,
            ["AWS_SECRET_ACCESS_KEY"] = "aws-credential",
            ["CLAUDE_CODE_MESSAGING_TOKEN"] = Sentinel,
            ["WIN_AGENT_TEAMS_TOKEN"] = Sentinel,
            ["AGENT_SESSION_ID"] = Sentinel,
            ["HERDR_SOCKET_PATH"] = Sentinel,
            ["HERDR_PANE_ID"] = Sentinel,
            ["LC_ALL"] = "C.UTF-8",
        };

        // Even an explicit opt-in cannot forward agent-session or Herdr caller context.
        var targeted = HerdrLaunchFixture.CommandStartInfo(seed, "CLAUDE_CODE_MESSAGING_TOKEN,HERDR_SOCKET_PATH,AGENT_SESSION_ID", "/run/owned.sock", "workspace", "list");
        var global = HerdrLaunchFixture.CommandStartInfo(seed, "HERDR_PANE_ID", null, "session", "list", "--json");
        var server = HerdrLaunchFixture.OwnedServerStartInfo(Sessions(), Session, seed, null);

        foreach (var env in new[] { targeted.Environment, global.Environment, server.Environment })
        {
            Assert.DoesNotContain(env.Values, v => v?.Contains(Sentinel, StringComparison.Ordinal) == true);
            Assert.Equal("/usr/bin", env["PATH"]);
            Assert.Equal("C.UTF-8", env["LC_ALL"]);
        }
        Assert.Equal("/run/owned.sock", targeted.Environment["HERDR_SOCKET_PATH"]);
        Assert.False(global.Environment.ContainsKey("HERDR_SOCKET_PATH"));
        Assert.False(server.Environment.ContainsKey("HERDR_SOCKET_PATH"));
    }

    [Fact]
    public void Environment_KeepsClaudeProviderAndCertificateConfiguration()
    {
        var seed = new Dictionary<string, string?>(BaseEnv)
        {
            ["CLAUDE_CODE_GIT_BASH_PATH"] = "C:\\Git\\bin\\bash.exe",
            ["CLAUDE_CODE_USE_BEDROCK"] = "1",
            ["CLAUDE_CODE_USE_VERTEX"] = "1",
            ["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = "2048",
            ["CLAUDE_CODE_SANDBOXED"] = "1",
            ["CLAUDE_CODE_SESSION_ID"] = "parent-session",
            ["CLAUDE_CODE_ENTRYPOINT"] = "parent",
            ["AWS_PROFILE"] = "team",
            ["AWS_SECRET_ACCESS_KEY"] = "aws-credential",
            ["GOOGLE_APPLICATION_CREDENTIALS"] = "/tmp/gcp.json",
            ["CLOUDSDK_CONFIG"] = "/tmp/cloudsdk",
            ["HTTPS_PROXY"] = "http://proxy",
            ["NODE_EXTRA_CA_CERTS"] = "/tmp/ca.pem",
        };

        var info = HerdrLaunchFixture.CommandStartInfo(seed, "CLAUDE_CODE_SESSION_ID", null, "session", "list");

        foreach (var key in new[]
        {
            "CLAUDE_CODE_GIT_BASH_PATH", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX",
            "CLAUDE_CODE_MAX_OUTPUT_TOKENS", "CLAUDE_CODE_SANDBOXED", "AWS_PROFILE",
            "AWS_SECRET_ACCESS_KEY", "GOOGLE_APPLICATION_CREDENTIALS", "CLOUDSDK_CONFIG",
            "HTTPS_PROXY", "NODE_EXTRA_CA_CERTS",
        })
        {
            Assert.Equal(seed[key], info.Environment[key]);
        }
        Assert.False(info.Environment.ContainsKey("CLAUDE_CODE_SESSION_ID"));
        Assert.False(info.Environment.ContainsKey("CLAUDE_CODE_ENTRYPOINT"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExistingUnownedSession_RefusesReuse(bool running)
    {
        var list = Sessions(("default", true), (Session, running));

        Assert.False(HerdrLaunchFixture.CanCreate(list, Session));
        Assert.Throws<InvalidOperationException>(() => HerdrLaunchFixture.OwnedServerStartInfo(list, Session, BaseEnv, null));
        Assert.True(HerdrLaunchFixture.CanCreate(list, "atf-spike-ffffffffffff"));

        // A same-name session without our record, server identity and owner label is never torn down.
        var owned = new OwnedSession("atf-owner-feedbeef", 10, 20);
        TeardownDecision[] refusals =
        [
            HerdrLaunchFixture.Teardown(null, Session, list, true, Workspaces("atf-owner-feedbeef")),
            HerdrLaunchFixture.Teardown(owned, Session, list, false, Workspaces("atf-owner-feedbeef")),
            HerdrLaunchFixture.Teardown(owned, Session, list, true, Workspaces("someone-else")),
            HerdrLaunchFixture.Teardown(owned, "atf-spike-gone", list, true, Workspaces("atf-owner-feedbeef")),
        ];
        Assert.All(refusals, d =>
        {
            Assert.NotNull(d.Problem);
            Assert.False(d.Stop);
        });

        var proven = HerdrLaunchFixture.Teardown(owned, Session, list, true, Workspaces("atf-owner-feedbeef"));
        Assert.Equal(running, proven.Stop);
        Assert.Equal(running, proven.Problem is null);
    }
}
