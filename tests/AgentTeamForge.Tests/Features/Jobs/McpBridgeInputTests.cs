using System.Text;
using System.Text.Json;
using AgentTeamForge.Host.Features.Jobs;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AgentTeamForge.Tests.Features.Jobs;

/// <summary>Bridge edge: unparsable JSON-RPC lines and tool arguments that do not fit the advertised schema.</summary>
public sealed class McpBridgeInputTests
{
    [Fact]
    public async Task Malformed_lines_get_a_parse_error_and_valid_lines_pass_through_unchanged()
    {
        const string valid = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";
        var raw = $"{valid}\n{{not json}}\n\n{{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\n{valid}\r\n{valid}";
        var replies = new List<JsonRpcMessage>();
        using var input = new McpStdioInput(new MemoryStream(Encoding.UTF8.GetBytes(raw)),
            (message, _) => { replies.Add(message); return Task.CompletedTask; });
        using var reader = new StreamReader(input);

        var passed = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.Equal($"{valid}\n\n{valid}\r\n{valid}", passed);
        var errors = replies.Cast<JsonRpcError>().ToList();
        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.Equal((int)McpErrorCode.ParseError, error.Error.Code));
        // JSON-RPC 2.0: an id that cannot be read is null on the wire; a recoverable one is echoed.
        Assert.Contains("\"id\":null", JsonSerializer.Serialize(errors[0], McpJsonUtilities.DefaultOptions.GetTypeInfo<JsonRpcMessage>()), StringComparison.Ordinal);
        Assert.Equal(new RequestId(5), errors[1].Id);
    }

    [Fact]
    public async Task An_unpaired_surrogate_escape_is_an_invalid_params_error_naming_the_field()
    {
        const string call = """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"submit_job","arguments":{"instruction":"x\ud800y"}}}""";
        const string notice = """{"jsonrpc":"2.0","method":"notifications/x","params":{"note":"\udc00"}}""";
        const string paired = """{"jsonrpc":"2.0","id":10,"method":"ping","params":{"emoji":"\ud83d\ude00"}}""";
        var replies = new List<JsonRpcMessage>();
        using var input = new McpStdioInput(new MemoryStream(Encoding.UTF8.GetBytes($"{call}\n{notice}\n{paired}\n")),
            (message, _) => { replies.Add(message); return Task.CompletedTask; });
        using var reader = new StreamReader(input);

        Assert.Equal($"{paired}\n", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        var error = Assert.IsType<JsonRpcError>(Assert.Single(replies)); // a notification gets no reply
        Assert.Equal(new RequestId(9), error.Id);
        Assert.Equal((int)McpErrorCode.InvalidParams, error.Error.Code);
        Assert.Contains("instruction", error.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_split_across_reads_is_judged_whole()
    {
        var replies = 0;
        using var input = new McpStdioInput(new TrickleStream(Encoding.UTF8.GetBytes("{\"a\":[1,2,3]}\n{\"b\":\n")),
            (_, _) => { replies++; return Task.CompletedTask; });
        using var reader = new StreamReader(input);

        Assert.Equal("{\"a\":[1,2,3]}\n", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, replies);
    }

    const string GetSchema = """{"type":"object","properties":{"job_id":{"type":"string"}},"required":["job_id"]}""";

    [Theory]
    [InlineData(GetSchema, """{"job_id":123}""", "job_id must be a string.")]
    [InlineData(GetSchema, """{}""", "Missing required argument job_id.")]
    [InlineData(GetSchema, """{"job_id":null}""", "Missing required argument job_id.")]
    [InlineData("""{"properties":{"limit":{"type":"integer","minimum":1,"maximum":50}}}""", """{"limit":0}""", "limit must be an integer from 1 to 50.")]
    [InlineData("""{"properties":{"timeout_s":{"type":"integer","minimum":1,"maximum":86400}}}""", """{"timeout_s":"abc"}""", "timeout_s must be an integer from 1 to 86400.")]
    [InlineData("""{"properties":{"timeout_s":{"type":"integer","minimum":1,"maximum":86400}}}""", """{"timeout_s":1e11}""", "timeout_s must be an integer from 1 to 86400.")]
    [InlineData("""{"properties":{"offset":{"type":"integer","minimum":0}}}""", """{"offset":-1}""", "offset must be an integer of at least 0.")]
    [InlineData("""{"properties":{"status":{"type":"string","enum":["queued","running"]}}}""", """{"status":"bogus"}""", "status must be one of: queued, running.")]
    [InlineData("""{"properties":{"worktree":{"type":"boolean"}}}""", """{"worktree":"yes"}""", "worktree must be true or false.")]
    [InlineData("""{"properties":{"expected_outputs":{"type":"array","items":{"type":"string"},"maxItems":1}}}""", """{"expected_outputs":["a","b"]}""", "expected_outputs must have at most 1 items.")]
    [InlineData("""{"properties":{"expected_outputs":{"type":"array","items":{"type":"string"}}}}""", """{"expected_outputs":[1]}""", "expected_outputs must contain only strings.")]
    [InlineData("""{"properties":{"job_id":{"type":"string"},"idempotency_key":{"type":"string"}},"dependentRequired":{"job_id":["idempotency_key"]}}""", """{"job_id":"j"}""", "Missing argument idempotency_key: it is required with job_id.")]
    [InlineData("""{"properties":{"name":{"type":"string"}},"additionalProperties":false}""", """{"name":"a","nmae":"b"}""", "Unknown argument nmae.")]
    public void Arguments_that_do_not_fit_the_schema_name_the_field(string schema, string args, string expected)
    {
        Assert.Equal(expected, McpArguments.Invalid(Element(schema), Arguments(args)));
    }

    [Theory]
    [InlineData(GetSchema, """{"job_id":"job_1","extra":1}""")]
    [InlineData("""{"properties":{"name":{"type":"string"}},"required":["name"]}""", """{"name":""}""")]
    [InlineData("""{"properties":{"model":{"type":"string"},"limit":{"type":"integer","minimum":1}}}""", """{"model":null,"limit":null}""")]
    public void Arguments_that_fit_the_schema_pass(string schema, string args)
    {
        Assert.Null(McpArguments.Invalid(Element(schema), Arguments(args)));
    }

    static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    static Dictionary<string, JsonElement> Arguments(string json) =>
        Element(json).EnumerateObject().ToDictionary(property => property.Name, property => property.Value);

    /// <summary>Returns one byte per read, so every line boundary falls between reads.</summary>
    sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
