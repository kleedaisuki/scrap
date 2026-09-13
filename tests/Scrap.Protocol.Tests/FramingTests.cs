using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Scrap.Protocol.Tests;

public sealed class FramingTests
{
    [Fact]
    public async Task RoundTrip_UsesLittleEndianPrefixAndStrictJson()
    {
        var source = ProtocolRequest.Create(
            "request-1",
            ProtocolMethods.RecordGet,
            new RecordGetParams("云", "密钥"));
        await using var stream = new MemoryStream();

        await LengthPrefixedJsonFraming.WriteAsync(stream, source);

        byte[] frame = stream.ToArray();
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(0, 4));
        Assert.Equal(frame.Length - 4, payloadLength);
        Assert.Equal((byte)'{', frame[4]);

        stream.Position = 0;
        ProtocolRequest actual = await LengthPrefixedJsonFraming.ReadAsync<ProtocolRequest>(stream);
        Assert.Equal(source.RequestId, actual.RequestId);
        Assert.Equal("云", actual.Params.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task ReadAsync_HandlesEveryReadBeingPartial()
    {
        await using var encoded = new MemoryStream();
        await LengthPrefixedJsonFraming.WriteAsync(
            encoded,
            ProtocolResponse.Success("r", new DaemonPingResult()));
        await using var partial = new ChunkedReadStream(encoded.ToArray(), maximumChunkSize: 1);

        ProtocolResponse response = await LengthPrefixedJsonFraming.ReadAsync<ProtocolResponse>(partial);

        Assert.IsType<DaemonPingResult>(response.GetResult<DaemonPingResult>());
    }

    [Fact]
    public async Task ReadAsync_RejectsLengthBeforeAllocatingPayload()
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, uint.MaxValue);
        await using var stream = new MemoryStream(header);

        ProtocolException exception = await Assert.ThrowsAsync<ProtocolException>(
            async () => await LengthPrefixedJsonFraming.ReadAsync<ProtocolRequest>(stream, maxFrameSize: 64));

        Assert.Equal(ProtocolErrorCodes.FrameTooLarge, exception.ErrorCode);
    }

    [Fact]
    public async Task WriteAsync_RejectsOversizedSerializedPayload()
    {
        await using var stream = new MemoryStream();

        ProtocolException exception = await Assert.ThrowsAsync<ProtocolException>(
            async () => await LengthPrefixedJsonFraming.WriteAsync(stream, new { text = "too long" }, 4));

        Assert.Equal(ProtocolErrorCodes.FrameTooLarge, exception.ErrorCode);
        Assert.Empty(stream.ToArray());
    }

    [Theory]
    [MemberData(nameof(InvalidUtf8Payloads))]
    public async Task ReadAsync_RejectsInvalidUtf8WithoutPayloadInException(byte[] payload)
    {
        await using var stream = Frame(payload);

        ProtocolException exception = await Assert.ThrowsAsync<ProtocolException>(
            async () => await LengthPrefixedJsonFraming.ReadAsync<JsonElement>(stream));

        Assert.Equal(ProtocolErrorCodes.InvalidUtf8, exception.ErrorCode);
        Assert.DoesNotContain(Convert.ToHexString(payload), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_RejectsInvalidJson()
    {
        await using var stream = Frame("{"u8.ToArray());

        ProtocolException exception = await Assert.ThrowsAsync<ProtocolException>(
            async () => await LengthPrefixedJsonFraming.ReadAsync<ProtocolRequest>(stream));

        Assert.Equal(ProtocolErrorCodes.InvalidJson, exception.ErrorCode);
    }

    [Fact]
    public async Task ReadAsync_DistinguishesCleanEofFromPartialHeader()
    {
        await using var empty = new MemoryStream();
        await Assert.ThrowsAsync<EndOfStreamException>(
            async () => await LengthPrefixedJsonFraming.ReadAsync<ProtocolRequest>(empty));

        await using var partial = new MemoryStream([1, 0]);
        ProtocolException exception = await Assert.ThrowsAsync<ProtocolException>(
            async () => await LengthPrefixedJsonFraming.ReadAsync<ProtocolRequest>(partial));
        Assert.Equal(ProtocolErrorCodes.InvalidRequest, exception.ErrorCode);
    }

    [Fact]
    public async Task ReadAsync_RejectsPartialPayload()
    {
        byte[] frame = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, 8);
        frame[4] = (byte)'{';
        frame[5] = (byte)'}';
        await using var stream = new MemoryStream(frame);

        ProtocolException exception = await Assert.ThrowsAsync<ProtocolException>(
            async () => await LengthPrefixedJsonFraming.ReadAsync<ProtocolRequest>(stream));

        Assert.Equal(ProtocolErrorCodes.InvalidRequest, exception.ErrorCode);
    }

    [Fact]
    public async Task ReadAsync_MapsEscapedUnpairedSurrogateToInvalidJson()
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            """{"scope":"s","key":"k","value":"\uD800","presentation":"masked"}""");
        await using var stream = Frame(payload);

        ProtocolException exception = await Assert.ThrowsAsync<ProtocolException>(
            async () => await LengthPrefixedJsonFraming.ReadAsync<RecordSetParams>(stream));

        Assert.Equal(ProtocolErrorCodes.InvalidJson, exception.ErrorCode);
    }

    public static TheoryData<byte[]> InvalidUtf8Payloads
    {
        get
        {
            var data = new TheoryData<byte[]>();
            data.Add([0xC3, 0x28]);
            data.Add([0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}']);
            return data;
        }
    }

    private static MemoryStream Frame(byte[] payload)
    {
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, checked((uint)payload.Length));
        payload.CopyTo(frame, 4);
        return new MemoryStream(frame);
    }

    private sealed class ChunkedReadStream(byte[] contents, int maximumChunkSize) : MemoryStream(contents)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumChunkSize)], cancellationToken);
    }
}
