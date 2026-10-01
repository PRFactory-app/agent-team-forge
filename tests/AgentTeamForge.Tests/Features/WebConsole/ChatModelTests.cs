using System.Text.Json.Nodes;
using AgentTeamForge.Host.Features.WebConsole;
using Jint;

namespace AgentTeamForge.Tests.Features.WebConsole;

public sealed class ChatModelTests
{
    static string Asset(string name)
    {
        using var stream = typeof(WebConsoleServer).Assembly.GetManifestResourceStream("WebConsole." + name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Runs `expression` (JS, may use `input` parsed from JSON) against lib.js and returns its JSON result.
    static JsonNode Eval(string expression, string inputJson = "null")
    {
        var engine = new Engine(o => o.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute(Asset("lib.js"));
        engine.SetValue("raw", inputJson);
        engine.Execute("var input = JSON.parse(raw);");
        return JsonNode.Parse(engine.Evaluate($"JSON.stringify({expression})").AsString())!;
    }

    [Fact]
    public void Thread_chain_orders_turns_oldest_first_and_reports_a_missing_parent()
    {
        const string jobs = """
            [{"job_id":"c","parent_job_id":"b","accepted_at":"3"},
             {"job_id":"a","parent_job_id":null,"accepted_at":"1"},
             {"job_id":"b","parent_job_id":"a","accepted_at":"2"}]
            """;
        var full = Eval("AtfLib.threadChain(input)", jobs);
        Assert.Equal(["a", "b", "c"], full["chain"]!.AsArray().Select(j => (string)j!["job_id"]!));
        Assert.Null(full["missing"]);

        var partial = Eval("AtfLib.threadChain(input.filter(j => j.job_id !== 'a'))", jobs);
        Assert.Equal(["b", "c"], partial["chain"]!.AsArray().Select(j => (string)j!["job_id"]!));
        Assert.Equal("a", (string)partial["missing"]!);
    }

    [Fact]
    public void Thread_chain_stops_at_a_cycle_and_at_the_cap()
    {
        var cycle = Eval("AtfLib.threadChain(input)", """[{"job_id":"a","parent_job_id":"b","accepted_at":"2"},{"job_id":"b","parent_job_id":"a","accepted_at":"1"}]""");
        Assert.Equal(2, cycle["chain"]!.AsArray().Count);

        var capped = Eval(
            "AtfLib.threadChain(Array.from({length: 40}, (_, i) => ({job_id: 'j' + i, parent_job_id: i ? 'j' + (i - 1) : null, accepted_at: String(i).padStart(3, '0')})), 'j39', 30)");
        Assert.Equal(30, capped["chain"]!.AsArray().Count);
        Assert.True((bool)capped["truncated"]!);
        Assert.Equal("j39", (string)capped["chain"]![29]!["job_id"]!);
    }

    [Fact]
    public void Agents_are_one_row_per_session_and_sit_under_their_lead()
    {
        const string jobs = """
            [{"job_id":"j1","session_id":"s1","lead_session_id":"L","accepted_at":"1","updated_at":"1"},
             {"job_id":"j2","session_id":"s1","lead_session_id":null,"accepted_at":"2","updated_at":"2"},
             {"job_id":"j3","session_id":null,"lead_session_id":"L","accepted_at":"3","updated_at":"3"},
             {"job_id":"j4","session_id":"s4","connector":true,"accepted_at":"4","updated_at":"4"},
             {"job_id":"j5","session_id":"s5","accepted_at":"5","updated_at":"5"}]
            """;
        var groups = Eval("AtfLib.groupAgents(input, ['L2'])", jobs).AsArray();

        var lead = groups.Single(g => (string)g!["id"]! == "L")!;
        Assert.Equal("lead", (string)lead["kind"]!);
        var agents = lead["agents"]!.AsArray();
        Assert.Equal(2, agents.Count);
        var shared = agents.Single(a => (string)a!["key"]! == "agent:s1")!;
        Assert.Equal(2, shared["jobs"]!.AsArray().Count);
        Assert.Equal("j2", (string)shared["newest"]!["job_id"]!);
        Assert.Contains(agents, a => (string)a!["key"]! == "agent:j3");

        Assert.Contains(groups, g => (string)g!["kind"]! == "prfactory");
        Assert.Contains(groups, g => (string)g!["kind"]! == "unassigned");
        Assert.Empty(groups.Single(g => (string)g!["id"]! == "L2")!["agents"]!.AsArray());
    }

    [Fact]
    public void Connector_names_become_ticket_phase_member_and_clipped_names_have_no_key()
    {
        var full = Eval("AtfLib.connectorParts('DEMOTENANT-1_refinement_lead')");
        Assert.Equal("DEMOTENANT-1 · refinement · lead", (string)full["display"]!);
        Assert.Equal("DEMOTENANT-1", (string)full["key"]!);
        Assert.Equal("refinement", (string)full["phase"]!);

        var clipped = Eval("AtfLib.connectorParts('x_ab12cd')");
        Assert.Null(clipped["key"]);
        Assert.Null(clipped["phase"]);
    }

    [Fact]
    public void Page_element_ids_are_unique()
    {
        var html = Asset("index.html");
        var ids = System.Text.RegularExpressions.Regex.Matches(html, "\\sid=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Contains("chat-view", ids);
    }
}
