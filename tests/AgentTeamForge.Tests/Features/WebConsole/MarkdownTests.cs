using System.Reflection;
using System.Text.Json.Nodes;
using AgentTeamForge.Host.Features.WebConsole;
using Jint;

namespace AgentTeamForge.Tests.Features.WebConsole;

public sealed class MarkdownTests
{
    static readonly string[] AllowedTypes =
        ["h", "p", "ul", "ol", "li", "code", "pre", "table", "tr", "th", "td", "strong", "em", "a", "text", "br"];

    static readonly Assembly Host = typeof(WebConsoleServer).Assembly;

    static string Asset(string name)
    {
        using var stream = Host.GetManifestResourceStream("WebConsole." + name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    static JsonArray Parse(string markdown)
    {
        var engine = new Engine(o => o.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute(Asset("lib.js"));
        engine.SetValue("input", markdown);
        return JsonNode.Parse(engine.Evaluate("JSON.stringify(AtfLib.parseMarkdown(input))").AsString())!.AsArray();
    }

    static IEnumerable<JsonNode> Walk(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array.SelectMany(Walk))
            {
                yield return item;
            }
        }
        else if (node is JsonObject obj)
        {
            yield return obj;
            foreach (var item in Walk(obj["c"]))
            {
                yield return item;
            }
        }
    }

    static string[] Types(JsonArray tree) => [.. Walk(tree).Select(n => (string)n["t"]!)];

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    public void Raw_html_is_literal_text(string html)
    {
        var tree = Parse(html);

        Assert.All(Types(tree).Where(t => t != "p"), t => Assert.Equal("text", t));
        Assert.Equal(html, string.Concat(Walk(tree).Where(n => (string)n["t"]! == "text").Select(n => (string)n["v"]!)));
    }

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](JaVaScRiPt:alert(1))")]
    [InlineData("[x](  javascript:alert(1))")]
    [InlineData("[x](data:text/html,<b>)")]
    [InlineData("[x](vbscript:msgbox)")]
    [InlineData("[x](//evil.example)")]
    [InlineData("[x](http://a b)")]
    public void Hostile_links_are_text(string markdown)
    {
        Assert.DoesNotContain("a", Types(Parse(markdown)));
    }

    [Fact]
    public void Http_links_and_autolinks_become_anchors()
    {
        var links = Walk(Parse("[ok](https://example.com) and see http://example.org/x.")).Where(n => (string)n["t"]! == "a").ToArray();

        Assert.Equal(["https://example.com", "http://example.org/x"], links.Select(n => (string)n["href"]!));
    }

    [Fact]
    public void Images_stay_literal_text()
    {
        var tree = Parse("![x](https://example.com/a.png)");

        Assert.DoesNotContain("a", Types(tree));
        Assert.Contains("![x](https://example.com/a.png)", tree.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Supported_subset_is_parsed()
    {
        var types = Types(Parse("""
            # Title

            - one
              - nested
            - two with **bold**, *it* and `<b>`

            ```
            <b>verbatim</b>
            ```

            | a | b |
            |---|---|
            | 1 | 2 |
            """));

        foreach (var expected in new[] { "h", "ul", "li", "strong", "em", "code", "pre", "table", "th", "td" })
        {
            Assert.Contains(expected, types);
        }
        Assert.Equal(2, types.Count(t => t == "ul"));
        var fence = Walk(Parse("```\n<b>verbatim</b>\n```")).Single();
        Assert.Equal("<b>verbatim</b>", (string)fence["v"]!);
    }

    [Fact]
    public void Output_only_contains_whitelisted_node_types()
    {
        var fixture = string.Join("\n", "# h <i>", "<div>x</div> **b *i* `c`** [l](https://a.b) ![i](x)", "1. a", "2) b", "| x |", "|--|", "| <u> |",
            "~~~", "```", "[[[[**__", "> quote", "---", "&amp; <script>");

        Assert.All(Types(Parse(fixture)), t => Assert.Contains(t, AllowedTypes));
    }

    [Fact]
    public void Pathological_input_is_fast()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var unit in new[] { "[", "**", "`", "*a ", "_", "[a](", "| a ", "  ", "# ", "http://" })
        {
            Parse(string.Concat(Enumerable.Repeat(unit, 10000)));
        }
        Parse(string.Join("\n", Enumerable.Repeat("[[[[[ ** `` _x", 1500)));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), clock.Elapsed.ToString());
    }

    [Fact]
    public void Wide_table_with_many_short_rows_falls_back_to_literal_pre()
    {
        var input = "|" + string.Concat(Enumerable.Repeat("h|", 2000)) + "\n|" + string.Concat(Enumerable.Repeat("-|", 2000)) + "\n"
            + string.Concat(Enumerable.Repeat("|\n", 2000));

        var tree = Parse(input);

        Assert.DoesNotContain("td", Types(tree));
        Assert.Equal("pre", (string)tree[0]!["t"]!);
    }

    [Fact]
    public void Many_overlong_autolink_lines_are_fast_and_literal()
    {
        var input = string.Join("\n", Enumerable.Repeat(string.Concat(Enumerable.Repeat("http://", 2000)), 10));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var tree = Parse(input);

        Assert.DoesNotContain("a", Types(tree));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
    }

    [Fact]
    public void Fenced_code_keeps_tabs_verbatim()
    {
        var fence = Walk(Parse("```\nall:\n\tgo build\t# x\n```")).Single();

        Assert.Equal("all:\n\tgo build\t# x", (string)fence["v"]!);
    }

    [Fact]
    public void Huge_input_is_one_pre_node()
    {
        var tree = Parse(new string('x', 200_001));

        Assert.Equal("pre", (string)Assert.Single(tree)!["t"]!);
    }

    [Fact]
    public void Browser_scripts_never_use_html_sinks()
    {
        string[] banned = ["innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(", "new Function", "srcdoc"];
        var scripts = Host.GetManifestResourceNames().Where(n => n.StartsWith("WebConsole.", StringComparison.Ordinal) && n.EndsWith(".js", StringComparison.Ordinal)).ToArray();

        Assert.Contains("WebConsole.lib.js", scripts);
        foreach (var script in scripts)
        {
            var source = Asset(script["WebConsole.".Length..]);
            Assert.All(banned, b => Assert.DoesNotContain(b, source, StringComparison.Ordinal));
        }
    }
}
