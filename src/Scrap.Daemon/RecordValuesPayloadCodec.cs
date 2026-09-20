using System.Buffers.Binary;
using System.Text;
using Scrap.Crypto;
using Scrap.Domain;

namespace Scrap.Daemon;

/// <summary>
/// 在一个 AEAD plaintext 中编码有序 value 列表，并无歧义地读取旧版标量 plaintext。<br/>
/// Encodes an ordered value list in one AEAD plaintext and unambiguously reads legacy scalar plaintexts.
/// </summary>
/// <remarks>
/// 标记以无效 UTF-8 字节 <c>0xff</c> 开始，因此任何有效旧版文本都不可能被误判为列表。
/// / The marker begins with invalid UTF-8 byte <c>0xff</c>, so no valid legacy text can be mistaken for a list.
/// </remarks>
internal static class RecordValuesPayloadCodec
{
    private static readonly byte[] Marker = [0xff, (byte)'S', (byte)'R', (byte)'V', 1];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>将已验证列表确定性编码为 length-prefixed payload。 / Deterministically encodes a validated list as a length-prefixed payload.</summary>
    internal static byte[] Encode(RecordValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var lengths = new int[values.Count];
        var total = Marker.Length + sizeof(byte) + (sizeof(int) * values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            lengths[index] = StrictUtf8.GetByteCount(values[index].Value);
            total = checked(total + lengths[index]);
        }

        var payload = new byte[total];
        Marker.CopyTo(payload, 0);
        payload[Marker.Length] = checked((byte)values.Count);
        var offset = Marker.Length + sizeof(byte);
        for (var index = 0; index < values.Count; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, sizeof(int)), lengths[index]);
            offset += sizeof(int);
            offset += StrictUtf8.GetBytes(values[index].Value, payload.AsSpan(offset, lengths[index]));
        }

        return payload;
    }

    /// <summary>解码新版列表，或把无标记的严格 UTF-8 旧 payload 作为单例。 / Decodes a new list, or treats an unmarked strict-UTF-8 legacy payload as a singleton.</summary>
    internal static IReadOnlyList<string> Decode(ReadOnlySpan<byte> payload)
    {
        if (!payload.StartsWith(Marker))
        {
            return [StrictUtf8.GetString(payload)];
        }

        var offset = Marker.Length;
        if (payload.Length <= offset)
        {
            throw InvalidPayload();
        }

        var count = payload[offset++];
        if (count is 0 or > RecordValues.MaximumCount)
        {
            throw InvalidPayload();
        }

        var values = new string[count];
        for (var index = 0; index < count; index++)
        {
            if (payload.Length - offset < sizeof(int)) throw InvalidPayload();
            var length = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, sizeof(int)));
            offset += sizeof(int);
            if (length < 0 || length > payload.Length - offset) throw InvalidPayload();
            values[index] = StrictUtf8.GetString(payload.Slice(offset, length));
            offset += length;
        }

        if (offset != payload.Length) throw InvalidPayload();
        return values;
    }

    private static EncryptedRecordFormatException InvalidPayload() =>
        new("Stored record value-list payload is invalid.");
}
