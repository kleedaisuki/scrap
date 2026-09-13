using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrap.Protocol;

/// <summary>
/// 强制所有协议字符串都是格式正确的 Unicode scalar sequence，防止 JSON serializer 将孤立 UTF-16 surrogate 静默替换为 U+FFFD。
/// / Requires every protocol string to be a well-formed Unicode scalar sequence, preventing the JSON serializer from silently replacing isolated UTF-16 surrogates with U+FFFD.
/// </summary>
internal sealed class ProtocolStringConverter : JsonConverter<string>
{
    /// <inheritdoc />
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ValidateEscapedSurrogates(GetRawValue(ref reader));
        string value = reader.GetString() ?? throw new JsonException("Protocol strings cannot be null.");
        ValidateUtf16(value);
        return value;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        ValidateUtf16(value);
        writer.WriteStringValue(value);
    }

    /// <inheritdoc />
    public override string ReadAsPropertyName(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => Read(ref reader, typeToConvert, options);

    /// <inheritdoc />
    public override void WriteAsPropertyName(
        Utf8JsonWriter writer,
        string value,
        JsonSerializerOptions options)
    {
        ValidateUtf16(value);
        writer.WritePropertyName(value);
    }

    private static ReadOnlySpan<byte> GetRawValue(ref Utf8JsonReader reader)
    {
        if (!reader.HasValueSequence)
        {
            return reader.ValueSpan;
        }

        return reader.ValueSequence.ToArray();
    }

    private static void ValidateUtf16(ReadOnlySpan<char> value)
    {
        while (!value.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(value, out _, out int charsConsumed);
            if (status != OperationStatus.Done)
            {
                throw InvalidUnicode();
            }

            value = value[charsConsumed..];
        }
    }

    private static void ValidateEscapedSurrogates(ReadOnlySpan<byte> rawValue)
    {
        for (int index = 0; index < rawValue.Length; index++)
        {
            if (rawValue[index] != (byte)'\\')
            {
                continue;
            }

            index = ValidateEscape(rawValue, index);
        }
    }

    private static int ValidateEscape(ReadOnlySpan<byte> value, int slashIndex)
    {
        int escapeIndex = slashIndex + 1;
        if (escapeIndex >= value.Length || value[escapeIndex] != (byte)'u')
        {
            return escapeIndex;
        }

        if (!TryReadHex(value, escapeIndex + 1, out int scalar))
        {
            return escapeIndex;
        }

        if (scalar is >= 0xDC00 and <= 0xDFFF)
        {
            throw InvalidUnicode();
        }

        if (scalar is < 0xD800 or > 0xDBFF)
        {
            return escapeIndex + 4;
        }

        int lowSlashIndex = escapeIndex + 5;
        if (!IsLowSurrogateEscape(value, lowSlashIndex))
        {
            throw InvalidUnicode();
        }

        return lowSlashIndex + 5;
    }

    private static bool IsLowSurrogateEscape(ReadOnlySpan<byte> value, int slashIndex)
    {
        return slashIndex + 5 < value.Length &&
            value[slashIndex] == (byte)'\\' &&
            value[slashIndex + 1] == (byte)'u' &&
            TryReadHex(value, slashIndex + 2, out int scalar) &&
            scalar is >= 0xDC00 and <= 0xDFFF;
    }

    private static bool TryReadHex(ReadOnlySpan<byte> value, int start, out int scalar)
    {
        scalar = 0;
        if (start + 4 > value.Length)
        {
            return false;
        }

        for (int index = start; index < start + 4; index++)
        {
            int digit = HexValue(value[index]);
            if (digit < 0)
            {
                return false;
            }

            scalar = (scalar << 4) | digit;
        }

        return true;
    }

    private static int HexValue(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
        >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
        >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
        _ => -1,
    };

    private static JsonException InvalidUnicode() =>
        new("Protocol strings must contain only well-formed Unicode scalar values.");
}
