using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AgentTeamForge.Host.Transport;

/// <summary>4-byte big-endian length + UTF-8 JSON. Size is checked before the body is read.</summary>
public static class Frames
{
    public static async Task WriteAsync<T>(Stream stream, T value, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, type);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>Reads one frame within <paramref name="timeout"/>; returns null on clean EOF before a header.</summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, JsonTypeInfo<T> type, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
        where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var header = new byte[4];
        if (!await FillAsync(stream, header, allowCleanEof: true, deadline.Token))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > maxBytes)
        {
            throw new FrameException(IpcProtocol.FrameTooLarge);
        }

        var body = new byte[length];
        await FillAsync(stream, body, allowCleanEof: false, deadline.Token);

        try
        {
            return JsonSerializer.Deserialize(body, type) ?? throw new FrameException(IpcProtocol.BadFrame);
        }
        catch (JsonException)
        {
            throw new FrameException(IpcProtocol.BadFrame);
        }
    }

    static async Task<bool> FillAsync(Stream stream, byte[] buffer, bool allowCleanEof, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                return offset == 0 && allowCleanEof ? false : throw new FrameException(IpcProtocol.BadFrame);
            }

            offset += read;
        }

        return true;
    }
}

public sealed class FrameException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
