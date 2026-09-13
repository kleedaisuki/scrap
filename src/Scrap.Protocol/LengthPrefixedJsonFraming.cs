using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Scrap.Protocol;

/// <summary>
/// 读写“4 字节小端无符号长度 + 严格 UTF-8 JSON”frame。
/// / Reads and writes frames composed of a four-byte little-endian unsigned length followed by strict UTF-8 JSON.
/// </summary>
/// <remarks>
/// 读取会在分配 payload 前检查长度。无效 frame 后调用方应关闭连接，因为流边界已不再可信。
/// / Reads check the length before allocating the payload. The caller should close the connection after an invalid frame because stream boundaries are no longer trustworthy.
/// </remarks>
/// <example>
/// client 在同一连接上串行写请求并读响应。 / A client serially writes a request and reads its response on one connection.
/// <code>
/// var request = ProtocolRequest.Create("opaque-id", ProtocolMethods.DaemonPing, new DaemonPingParams());
/// await LengthPrefixedJsonFraming.WriteAsync(stream, request, cancellationToken: cancellationToken);
/// var response = await LengthPrefixedJsonFraming.ReadAsync&lt;ProtocolResponse&gt;(
///     stream,
///     cancellationToken: cancellationToken);
/// response.GetResult&lt;DaemonPingResult&gt;();
/// </code>
/// </example>
public static class LengthPrefixedJsonFraming
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// 写入一个完整 frame。并发调用方必须在连接层串行化写入。
    /// / Writes one complete frame. Concurrent callers must serialize writes at the connection layer.
    /// </summary>
    /// <typeparam name="T">payload DTO 类型。 / Payload DTO type.</typeparam>
    /// <param name="stream">可写传输流。 / Writable transport stream.</param>
    /// <param name="value">待序列化 payload。 / Payload to serialize.</param>
    /// <param name="maxFrameSize">允许的最大 payload 字节数。 / Maximum permitted payload byte count.</param>
    /// <param name="cancellationToken">取消标记。取消可能留下部分 frame，调用方必须丢弃连接。 / Cancellation token. Cancellation may leave a partial frame, so the caller must discard the connection.</param>
    /// <returns>表示异步写入的任务。 / A task representing the asynchronous write.</returns>
    /// <exception cref="ProtocolException">序列化后的 payload 超过上限。 / The serialized payload exceeds the limit.</exception>
    public static async ValueTask WriteAsync<T>(
        Stream stream,
        T value,
        int maxFrameSize = ProtocolConstants.DefaultMaxFrameSize,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(value);
        ValidateMaximum(maxFrameSize);

        byte[] payload = ProtocolJson.Serialize(value);
        EnsurePayloadFits(payload.Length, maxFrameSize);

        byte[] header = new byte[ProtocolConstants.FrameHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读取并反序列化一个完整 frame，正确处理 transport 的 partial read。
    /// / Reads and deserializes one complete frame, correctly handling partial transport reads.
    /// </summary>
    /// <typeparam name="T">目标 DTO 类型。 / Target DTO type.</typeparam>
    /// <param name="stream">可读传输流。 / Readable transport stream.</param>
    /// <param name="maxFrameSize">允许的最大 payload 字节数。 / Maximum permitted payload byte count.</param>
    /// <param name="cancellationToken">取消标记。取消后调用方必须丢弃连接。 / Cancellation token. The caller must discard the connection after cancellation.</param>
    /// <returns>反序列化 DTO。 / Deserialized DTO.</returns>
    /// <exception cref="EndOfStreamException">在 frame 之间到达正常 EOF。 / Clean EOF was reached between frames.</exception>
    /// <exception cref="ProtocolException">frame 不完整、超长、含 BOM、UTF-8 无效或 JSON 无效。 / The frame is partial, oversized, BOM-prefixed, invalid UTF-8, or invalid JSON.</exception>
    public static async ValueTask<T> ReadAsync<T>(
        Stream stream,
        int maxFrameSize = ProtocolConstants.DefaultMaxFrameSize,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateMaximum(maxFrameSize);

        byte[] header = new byte[ProtocolConstants.FrameHeaderSize];
        await ReadHeaderAsync(stream, header, cancellationToken).ConfigureAwait(false);

        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
        EnsurePayloadFits(payloadLength, maxFrameSize);

        byte[] payload = new byte[payloadLength];
        await ReadPayloadAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return DeserializePayload<T>(payload);
    }

    private static async ValueTask ReadHeaderAsync(
        Stream stream,
        Memory<byte> header,
        CancellationToken cancellationToken)
    {
        int read = await ReadAtLeastOneAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            throw new EndOfStreamException("The transport ended between protocol frames.");
        }

        await FillRemainderAsync(stream, header, read, "Frame header is incomplete.", cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask ReadPayloadAsync(
        Stream stream,
        Memory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.IsEmpty)
        {
            return;
        }

        await FillRemainderAsync(stream, payload, 0, "Frame payload is incomplete.", cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<int> ReadAtLeastOneAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken) =>
        await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

    private static async ValueTask FillRemainderAsync(
        Stream stream,
        Memory<byte> buffer,
        int offset,
        string eofMessage,
        CancellationToken cancellationToken)
    {
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new ProtocolException(ProtocolErrorCodes.InvalidRequest, eofMessage);
            }

            offset += read;
        }
    }

    private static T DeserializePayload<T>(ReadOnlySpan<byte> payload) where T : notnull
    {
        EnsureNoByteOrderMark(payload);

        try
        {
            _ = StrictUtf8.GetCharCount(payload);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ProtocolException(
                ProtocolErrorCodes.InvalidUtf8,
                "Frame payload is not valid UTF-8.",
                exception);
        }

        try
        {
            return ProtocolJson.Deserialize<T>(payload);
        }
        catch (JsonException exception)
        {
            throw new ProtocolException(
                ProtocolErrorCodes.InvalidJson,
                "Frame payload is not valid protocol JSON.",
                exception);
        }
    }

    private static void EnsureNoByteOrderMark(ReadOnlySpan<byte> payload)
    {
        ReadOnlySpan<byte> utf8Bom = [0xEF, 0xBB, 0xBF];
        if (payload.StartsWith(utf8Bom))
        {
            throw new ProtocolException(
                ProtocolErrorCodes.InvalidUtf8,
                "A UTF-8 byte-order mark is not permitted in a protocol frame.");
        }
    }

    private static void EnsurePayloadFits(long payloadLength, int maxFrameSize)
    {
        if (payloadLength > maxFrameSize)
        {
            throw new ProtocolException(
                ProtocolErrorCodes.FrameTooLarge,
                $"Frame payload length exceeds the {maxFrameSize}-byte limit.");
        }
    }

    private static void ValidateMaximum(int maxFrameSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrameSize);
    }
}
