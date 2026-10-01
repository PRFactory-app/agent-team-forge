using System.Text.Json.Nodes;
using AgentTeamForge.Host.Features.WebConsole;
using Jint;

namespace AgentTeamForge.Tests.Features.WebConsole;

public sealed class SoundEventsTests
{
    static string Asset(string name)
    {
        using var stream = typeof(WebConsoleServer).Assembly.GetManifestResourceStream("WebConsole." + name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    static JsonNode Eval(string script)
    {
        var engine = new Engine(o => o.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute(Asset("lib.js"));
        return JsonNode.Parse(engine.Evaluate($"JSON.stringify((() => {{ {script} }})())").AsString())!;
    }

    const string Job = "const job = (id, status, extra = {}) => ({ job_id: id, status, light: status === 'running' ? 'green' : status === 'failed' ? 'red' : 'grey', ...extra });";

    [Fact]
    public void First_poll_only_seeds_and_transitions_pick_the_right_sound()
    {
        var r = Eval($$"""
            {{Job}}
            const first = AtfLib.soundEvents(null, [job('a', 'running'), job('b', 'running'), job('c', 'running'), job('d', 'running')]);
            const done = AtfLib.soundEvents(first.map, [job('a', 'completed'), job('b', 'running'), job('c', 'running'), job('d', 'running')]);
            const failed = AtfLib.soundEvents(first.map, [job('a', 'running'), job('b', 'failed')]);
            const recon = AtfLib.soundEvents(first.map, [job('c', 'needs_reconciliation', { light: 'yellow' })]);
            const login = AtfLib.soundEvents(first.map, [job('d', 'running', { reason_code: 'agent_login_required' })]);
            return { first: [first.done, first.attention], done: [done.done, done.attention], failed: [failed.done, failed.attention],
              recon: [recon.done, recon.attention], login: [login.done, login.attention] };
            """);
        Assert.Equal("[false,false]", r["first"]!.ToJsonString());
        Assert.Equal("[true,false]", r["done"]!.ToJsonString());
        Assert.Equal("[false,true]", r["failed"]!.ToJsonString());
        Assert.Equal("[false,true]", r["recon"]!.ToJsonString());
        Assert.Equal("[false,true]", r["login"]!.ToJsonString());
    }

    [Fact]
    public void History_first_seen_and_unchanged_jobs_never_sound()
    {
        var r = Eval($$"""
            {{Job}}
            const seed = AtfLib.soundEvents(null, [job('old', 'completed')]);
            const next = AtfLib.soundEvents(seed.map, [job('old', 'completed'), job('new', 'completed'), job('bad', 'failed')]);
            return [next.done, next.attention];
            """);
        Assert.Equal("[false,false]", r.ToJsonString());
    }

    [Fact]
    public void A_burst_is_one_sound_and_the_window_is_enforced_by_the_injected_clock()
    {
        var r = Eval($$"""
            {{Job}}
            const seed = AtfLib.soundEvents(null, [1, 2, 3].map(i => job('j' + i, 'running')));
            const ev = AtfLib.soundEvents(seed.map, [1, 2, 3].map(i => job('j' + i, 'completed')));
            const gate = {};
            const both = AtfLib.pickSound({}, { done: true, attention: true }, 0);
            return [AtfLib.pickSound(gate, ev, 1000), AtfLib.pickSound(gate, ev, 1500), AtfLib.pickSound(gate, ev, 2600), both];
            """);
        Assert.Equal("""["done",null,"done","attention"]""", r.ToJsonString());
    }
}
