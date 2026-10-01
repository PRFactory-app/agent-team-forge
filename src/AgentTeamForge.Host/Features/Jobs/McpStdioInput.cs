using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AgentTeamForge.Host.Features.Jobs;

/// <summary>
/// Stdin for the MCP bridge. The SDK silently drops a line that is not JSON unless it can still find a
/// request id, so the client never gets an answer; JSON-RPC 2.0 requires a -32700 parse error with
/// <c>"id": null</c>. A JSON escape can also carry an unpaired UTF-16 surrogate (<c>"\ud800"</c>) that no
/// string can hold; the SDK then fails inside its own parameter binding and answers a bare -32603. Each
/// complete line is checked here: valid JSON passes through unchanged; a malformed line is answered with
/// -32700 and an unencodable one with -32602 naming the field, through <paramref name="reply"/> (with the
/// request id when one can be recovered, as the SDK does; a notification gets no -32602 reply). Reads
/// honour cancellation like the SDK's own stdin wrapper, because console streams ignore tokens.
/// </summary>
internal sealed class McpStdioInput(Stream input, Func<JsonRpcMessage, CancellationToken, Task> reply) : Stream
{
    readonly byte[] _chunk = new byte[16 * 1024];
    readonly MemoryStream _line = new();
    readonly MemoryStream _passed = new();
    byte[] _ready = [];
    int _readyOffset;
    bool _eof;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_readyOffset >= _ready.Length)
        {
            if (_eof)
            {
                return 0;
            }

            var pending = input.ReadAsync(_chunk, cancellationToken);
            var read = pending.IsCompletedSuccessfully ? pending.Result : await pending.AsTask().WaitAsync(cancellationToken);
            if (read == 0)
            {
                // An unterminated last line is passed on as is; the SDK reads it the same way.
                _eof = true;
                _line.WriteTo(_passed);
                _line.SetLength(0);
            }
            else
            {
                await SplitAsync(_chunk.AsMemory(0, read), cancellationToken);
            }
            _ready = _passed.ToArray();
            _readyOffset = 0;
            _passed.SetLength(0);
        }

        var count = Math.Min(buffer.Length, _ready.Length - _readyOffset);
        _ready.AsMemory(_readyOffset, count).CopyTo(buffer);
        _readyOffset += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    async Task SplitAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        while (!data.IsEmpty)
        {
            var newline = data.Span.IndexOf((byte)'\n');
            if (newline < 0)
            {
                _line.Write(data.Span);
                return;
            }

            _line.Write(data.Span[..(newline + 1)]);
            data = data[(newline + 1)..];
            var line = _line.GetBuffer().AsMemory(0, (int)_line.Length);
            var error = Inspect(line.Span);
            if (error is null)
            {
                _passed.Write(line.Span);
            }
            else if (error.Error.Code == (int)McpErrorCode.ParseError || error.Id.Id is not null)
            {
                // Like the SDK's own error reply: a failed send must not end the read loop.
                try { await reply(error, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
            _line.SetLength(0);
        }
    }

    /// <summary>
    /// Null for a line to pass on: blank (the SDK skips it) or exactly one complete JSON value whose strings
    /// all decode. Otherwise the JSON-RPC error that answers it.
    /// </summary>
    internal static JsonRpcError? Inspect(ReadOnlySpan<byte> line)
    {
        if (line.Trim(" \t\r\n"u8).IsEmpty)
        {
            return null;
        }

        string? unencodable = null;
        try
        {
            // Depth limits stay the SDK's call: it answers an over-deep request with its own error.
            var reader = new Utf8JsonReader(line, new JsonReaderOptions { MaxDepth = 4096 });
            var field = "";
            while (reader.Read())
            {
                if (unencodable is not null)
                {
                    continue; // Finish the read: a syntax error later on the line still wins.
                }

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (Decodes(ref reader, out var name)) { field = name; }
                    else { unencodable = "a property name"; }
                }
                else if (reader.TokenType == JsonTokenType.String && reader.ValueIsEscaped && !Decodes(ref reader, out _))
                {
                    unencodable = field;
                }
            }
        }
        catch (JsonException)
        {
            return Error(line, McpErrorCode.ParseError, "Parse error: the line is not valid JSON.");
        }

        return unencodable is null ? null : Error(line, McpErrorCode.InvalidParams,
            $"Invalid params: {unencodable} contains an unpaired UTF-16 surrogate, which is not valid Unicode.");
    }

    static bool Decodes(ref Utf8JsonReader reader, out string text)
    {
        try
        {
            text = reader.GetString()!;
            return true;
        }
        catch (InvalidOperationException)
        {
            text = "";
            return false;
        }
    }

    static JsonRpcError Error(ReadOnlySpan<byte> line, McpErrorCode code, string message) => new()
    {
        Id = RecoverId(line),
        Error = new JsonRpcErrorDetail { Code = (int)code, Message = message },
    };

    /// <summary>The top-level "id" read before the syntax error, if any; default (JSON null) otherwise.</summary>
    static RequestId RecoverId(ReadOnlySpan<byte> line)
    {
        try
        {
            var reader = new Utf8JsonReader(line, new JsonReaderOptions { MaxDepth = 4096 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return default;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isId = reader.ValueTextEquals("id"u8);
                if (!reader.Read())
                {
                    break;
                }

                if (isId)
                {
                    return reader.TokenType switch
                    {
                        JsonTokenType.String => new RequestId(reader.GetString()!),
                        JsonTokenType.Number when reader.TryGetInt64(out var number) => new RequestId(number),
                        _ => default,
                    };
                }

                reader.Skip();
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }

        return default;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            input.Dispose();
            _line.Dispose();
            _passed.Dispose();
        }

        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
